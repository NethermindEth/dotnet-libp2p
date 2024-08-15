// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Stack;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core;

public class LocalPeer(IProtocolStackSettings protocolStackSettings, Identity identity) : IPeer
{

    protected IProtocolStackSettings protocolStackSettings = protocolStackSettings;

    public Identity Identity { get; } = identity;

    public ObservableCollection<Multiaddress> ListenAddresses { get; } = new();
    public ObservableCollection<Session> Sessions { get; } = new();
    public Multiaddress Address => throw new NotImplementedException();

    protected virtual Task ConnectedTo(ISession peer, bool isDialer) => Task.CompletedTask;

    public class Session(LocalPeer localPeer, bool isListener) : ISession
    {
        public string Id { get; } = Interlocked.Increment(ref Ids.IdCounter).ToString();
        public State State { get; } = new();

        public bool IsListener => isListener;
        public Multiaddress RemoteAddress => State.RemoteAddress ?? throw new Libp2pException("Session contains uninitialized remote address.");

        public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol
        {
            TaskCompletionSource tcs = new();
            SubDialRequests.Add(new ChannelRequest() { CompletionSource = tcs, SubProtocol = localPeer.GetProtocolInstance<TProtocol>() });
            return tcs.Task;
        }

        public Task DialAsync(ISessionProtocol[] protocols, CancellationToken token = default)
        {
            TaskCompletionSource tcs = new();
            SubDialRequests.Add(new ChannelRequest() { CompletionSource = tcs, SubProtocol = protocols[0] });
            return tcs.Task;
        }

        private readonly BlockingCollection<ChannelRequest> SubDialRequests = [];

        private CancellationTokenSource connectionTokenSource = new();
        public Task DisconnectAsync()
        {
            connectionTokenSource.Cancel();
            return Task.CompletedTask;
        }

        public CancellationToken ConnectionToken => connectionTokenSource.Token;


        private TaskCompletionSource ConnectedTcs = new();

        public Task Connected => ConnectedTcs.Task;

        internal IEnumerable<ChannelRequest> GetRequestQueue() => SubDialRequests.GetConsumingEnumerable(ConnectionToken);
    }

    protected virtual ProtocolRef SelectProtocol(Multiaddress addr)
    {
        if (protocolStackSettings.TopProtocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(protocolStackSettings)}");
        }

        if (protocolStackSettings.TopProtocols.Length is not 1)
        {
            throw new Libp2pSetupException("Top protocol should be single one by default");

        }

        return protocolStackSettings.TopProtocols.Single();
    }

    Dictionary<object, TaskCompletionSource<Multiaddress>> listenerReadyTcs = new();

    public event OnConnection OnConnection;

    public virtual async Task StartListenAsync(Multiaddress[] addrs, CancellationToken token = default)
    {
        List<Task> listenTasks = new(addrs.Length);

        foreach (Multiaddress addr in addrs)
        {
            ProtocolRef listenerProtocol = SelectProtocol(addr);

            if (listenerProtocol.Protocol is not ITransportProtocol transportProtocol)
            {
                throw new Libp2pSetupException($"{nameof(ITransportProtocol)} should be implemented by {listenerProtocol.GetType()}");
            }

            ITransportContext ctx = new TransportContext(this, listenerProtocol, true);
            TaskCompletionSource<Multiaddress> tcs = new();
            listenerReadyTcs[ctx] = tcs;

            _ = transportProtocol.ListenAsync(ctx, addr, token).ContinueWith(t => ListenAddresses.Remove(tcs.Task.Result));

            listenTasks.Add(tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(5000)));
            ListenAddresses.Add(tcs.Task.Result);
        }

        await Task.WhenAll(listenTasks);
    }

    public void ListenerReady(object sender, Multiaddress addr)
    {
        if (listenerReadyTcs.Remove(sender, out TaskCompletionSource<Multiaddress>? tcs))
        {
            tcs.SetResult(addr);
        }
    }

    public INewConnectionContext CreateConnection(ProtocolRef proto, bool isListener)
    {
        Session session = new(this, isListener);
        return new NewConnectionContext(this, session, proto, isListener);
    }

    public INewSessionContext CreateSession(Session session, ProtocolRef proto, bool isListener)
    {
        if (session.State.RemoteAddress?.GetPeerId() is null)
        {
            throw new Libp2pSetupException($"{nameof(session.State.RemoteAddress)} should be initialiazed before session creation");
        }

        lock (Sessions)
        {
            if (Sessions.Any(s => !ReferenceEquals(session, s) && s.State.RemoteAddress.GetPeerId() == session.State.RemoteAddress?.GetPeerId()))
            {
                throw new Libp2pException("Session is already established");
            }

            Sessions.Add(session);
        }
        return new NewSessionContext(this, session, proto, isListener);
    }

    internal IEnumerable<IProtocol> GetProtocolsFor(ProtocolRef protocol)
    {
        if (protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(protocolStackSettings)}");
        }

        if (!protocolStackSettings.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is noty added");
        }

        return protocolStackSettings.Protocols[protocol].Select(p => p.Protocol);
    }

    internal IProtocol? GetProtocolInstance<TProtocol>()
    {
        return protocolStackSettings.Protocols?.Keys.FirstOrDefault(p => p.Protocol.GetType() == typeof(TProtocol))?.Protocol;
    }

    public async Task<ISession> DialAsync(Multiaddress addr, CancellationToken token = default)
    {
        ProtocolRef dialerProtocol = SelectProtocol(addr);

        if (dialerProtocol.Protocol is not ITransportProtocol transportProtocol)
        {
            throw new Libp2pSetupException($"{nameof(ITransportProtocol)} should be implemented by {dialerProtocol.GetType()}");
        }

        Session session = new(this, false);
        INewConnectionContext ctx = new NewConnectionContext(this, session, dialerProtocol, session.IsListener);

        _ = transportProtocol.DialAsync(ctx, addr, token);

        await session.Connected;
        return session;
    }

    internal IChannel Upgrade(LocalPeer localPeer, Session session, ProtocolRef protocol, IProtocol? upgradeProtocol, UpgradeOptions? options)
    {
        if (protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(protocolStackSettings)}");
        }

        if (!protocolStackSettings.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is noty added");
        }

        ProtocolRef top = upgradeProtocol is not null ? new ProtocolRef(upgradeProtocol) : protocolStackSettings.Protocols[protocol].First();

        Channel res = new Channel();

        bool isListener = session.IsListener && options?.ModeOverride != UpgradeModeOverride.Dial;

        switch (top.Protocol)
        {
            case IConnectionProtocol tProto:
                {
                    var ctx = new ConnectionContext(this, session, top, isListener);
                    _ = isListener ? tProto.ListenAsync(res.Reverse, ctx) : tProto.DialAsync(res.Reverse, ctx);
                    break;
                }
            case ISessionProtocol sProto:
                {
                    var ctx = new SessionContext(this, session, top, isListener);
                    _ = isListener ? sProto.ListenAsync(res.Reverse, ctx) : sProto.DialAsync(res.Reverse, ctx);
                    break;
                }
        }

        return res;
    }

    internal async Task Upgrade(LocalPeer localPeer, Session session, IChannel parentChannel, ProtocolRef protocol, IProtocol? upgradeProtocol, UpgradeOptions? options)
    {
        if (protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(protocolStackSettings)}");
        }

        ProtocolRef top = upgradeProtocol is not null ? new ProtocolRef(upgradeProtocol) : protocolStackSettings.Protocols[protocol].First();
        bool isListener = session.IsListener && options?.ModeOverride != UpgradeModeOverride.Dial;

        switch (top.Protocol)
        {
            case IConnectionProtocol tProto:
                {
                    var ctx = new ConnectionContext(this, session, top, isListener) { UpgradeOptions = options };
                    await tProto.DialAsync(parentChannel, ctx);
                    break;
                }
            case ISessionProtocol sProto:
                {
                    var ctx = new SessionContext(this, session, top, isListener) { UpgradeOptions = options };
                    await sProto.DialAsync(parentChannel, ctx);
                    break;
                }
        }
    }
}

public class NewSessionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener) : ContextBase(localPeer, session, protocol, isListener), INewSessionContext
{
    public IEnumerable<ChannelRequest> DialRequests => session.GetRequestQueue();

    public string Id { get; } = Interlocked.Increment(ref Ids.IdCounter).ToString();

    public CancellationToken Token => session.ConnectionToken;

    public void Dispose()
    {

    }
}

public class SessionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener) : ContextBase(localPeer, session, protocol, isListener), ISessionContext
{
    public UpgradeOptions? UpgradeOptions { get; init; }

    public Task DialAsync<TProtocol>() where TProtocol : ISessionProtocol
    {
        return session.DialAsync<TProtocol>();
    }

    public Task DialAsync(ISessionProtocol[] protocols)
    {
        return session.DialAsync(protocols);
    }

    public Task DisconnectAsync()
    {
        return session.DisconnectAsync();
    }
}

public class NewConnectionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener) : ContextBase(localPeer, session, protocol, isListener), INewConnectionContext
{
    public CancellationToken Token => session.ConnectionToken;

    public void Dispose()
    {

    }
}

public class ConnectionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener) : ContextBase(localPeer, session, protocol, isListener), IConnectionContext
{
    public UpgradeOptions? UpgradeOptions { get; init; }


    public Task DisconnectAsync()
    {
        return session.DisconnectAsync();
    }
}

public class ContextBase(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener) : IChannelFactory
{
    protected bool isListener = isListener;
    public IPeer Peer => localPeer;
    public State State => session.State;

    public IEnumerable<IProtocol> SubProtocols => localPeer.GetProtocolsFor(protocol);

    public string Id { get; } = session.Id;

    protected LocalPeer localPeer = localPeer;
    protected LocalPeer.Session session = session;
    protected ProtocolRef protocol = protocol;

    public IChannel Upgrade(UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(localPeer, session, protocol, null, upgradeOptions);
    }

    public Task Upgrade(IChannel parentChannel, UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(localPeer, session, parentChannel, protocol, null, upgradeOptions);
    }

    public IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(localPeer, session, protocol, specificProtocol, upgradeOptions);
    }

    public Task Upgrade(IChannel parentChannel, IProtocol specificProtocol, UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(localPeer, session, parentChannel, protocol, specificProtocol, upgradeOptions);
    }

    public INewConnectionContext CreateConnection()
    {
        return localPeer.CreateConnection(protocol, session.IsListener);
    }
    public INewSessionContext UpgradeToSession()
    {
        return localPeer.CreateSession(session, protocol, isListener);
    }

    public void ListenerReady(Multiaddress addr)
    {
        localPeer.ListenerReady(this, addr);
    }
}
