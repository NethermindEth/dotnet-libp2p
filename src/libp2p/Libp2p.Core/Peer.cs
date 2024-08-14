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
        public bool IsListener => isListener;
        public Multiaddress RemoteAddress => Remote.Address ?? throw new Libp2pException("Session contains uninitialized remote address.");
        public Multiaddress Address { get; set; }
        public Remote Remote { get; } = new Remote();

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

    protected virtual IProtocol SelectProtocol(Multiaddress addr)
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

    Dictionary<ITransportContext, TaskCompletionSource<Multiaddress>> listenerReadyTcs = new();

    public event OnConnection OnConnection;

    public virtual async Task StartListenAsync(Multiaddress[] addrs, CancellationToken token = default)
    {
        List<Task> listenTasks = new(addrs.Length);

        foreach (Multiaddress addr in addrs)
        {
            IProtocol listenerProtocol = SelectProtocol(addr);

            if (listenerProtocol is not ITransportProtocol transportProtocol)
            {
                throw new Libp2pSetupException($"{nameof(ITransportProtocol)} should be implemented by {listenerProtocol.GetType()}");
            }

            ITransportContext ctx = new TransportContext(this, transportProtocol, true);
            TaskCompletionSource<Multiaddress> tcs = new();
            listenerReadyTcs[ctx] = tcs;

            _ = transportProtocol.ListenAsync(ctx, addr, token).ContinueWith(t => ListenAddresses.Remove(tcs.Task.Result));

            listenTasks.Add(tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(5000)));
            ListenAddresses.Add(tcs.Task.Result);
        }

        await Task.WhenAll(listenTasks);
    }

    public void ListenerReady(ITransportContext sender, Multiaddress addr)
    {
        if (listenerReadyTcs.Remove(sender, out TaskCompletionSource<Multiaddress>? tcs))
        {
            tcs.SetResult(addr);
        }
    }

    public ITransportConnectionContext CreateConnection(ITransportProtocol proto, bool isListener)
    {
        Session session = new(this, isListener);
        return new TransportConnectionContext(this, session, proto);
    }

    public IConnectionSessionContext CreateSession(Session session)
    {
        if (session.Remote.Address?.GetPeerId() is null)
        {
            throw new Libp2pSetupException($"{nameof(session.Remote)} should be initialiazed before session creation");
        }

        lock (Sessions)
        {
            if (Sessions.Any(s => !ReferenceEquals(session, s) && s.Remote.Address?.GetPeerId() == session.Remote.Address?.GetPeerId()))
            {
                throw new Libp2pException("Session is already established");
            }

            Sessions.Add(session);
        }
        return new ConnectionSessionContext(this, session);
    }

    internal IEnumerable<IProtocol> GetProtocolsFor(IProtocol protocol)
    {
        if (protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(protocolStackSettings)}");
        }

        if (!protocolStackSettings.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is noty added");
        }

        return protocolStackSettings.Protocols[protocol];
    }

    internal IProtocol? GetProtocolInstance<TProtocol>()
    {
        return protocolStackSettings.Protocols?.Keys.FirstOrDefault(p => p.GetType() == typeof(TProtocol));
    }

    public async Task<ISession> DialAsync(Multiaddress addr, CancellationToken token = default)
    {
        IProtocol dialerProtocol = SelectProtocol(addr);

        if (dialerProtocol is not ITransportProtocol transportProtocol)
        {
            throw new Libp2pSetupException($"{nameof(ITransportProtocol)} should be implemented by {dialerProtocol.GetType()}");
        }

        Session session = new(this, false);
        ITransportConnectionContext ctx = new TransportConnectionContext(this, session, transportProtocol);

        _ = transportProtocol.DialAsync(ctx, addr, token);

        await session.Connected;
        return session;
    }

    internal IChannel Upgrade(LocalPeer localPeer, Session session, IProtocol protocol, IProtocol? upgradeProtocol, UpgradeOptions? options)
    {
        if (protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(protocolStackSettings)}");
        }

        if (!protocolStackSettings.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is noty added");
        }

        IProtocol top = upgradeProtocol ?? protocolStackSettings.Protocols[protocol].First();

        Channel res = new Channel();
        if (top is IConnectionProtocol tProto)
        {
            var ctx = new ConnectionContext(this, session, top);
            _ = session.IsListener ? tProto.ListenAsync(res.Reverse, ctx) : tProto.DialAsync(res.Reverse, ctx);
        }

        if (top is ISessionProtocol sProto)
        {
            var ctx = new SessionContext(this, session, top);
            _ = session.IsListener ? sProto.ListenAsync(res.Reverse, ctx) : sProto.DialAsync(res.Reverse, ctx);
        }
        return res;
    }

    internal async Task Upgrade(LocalPeer localPeer, Session session, IChannel parentChannel, IProtocol protocol, IProtocol? upgradeProtocol, UpgradeOptions? options)
    {
        if (protocolStackSettings.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(protocolStackSettings)}");
        }

        if (!protocolStackSettings.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is noty added");
        }

        IProtocol top = upgradeProtocol ?? protocolStackSettings.Protocols[protocol].First();

        if (top is IConnectionProtocol tProto)
        {
            var ctx = new ConnectionContext(this, session, top) { UpgradeOptions = options };
            await tProto.DialAsync(parentChannel, ctx);
        }
        if (top is ISessionProtocol sProto)
        {
            var ctx = new SessionContext(this, session, top) { UpgradeOptions = options };
            await sProto.DialAsync(parentChannel, ctx);
        }
    }

    internal void DisposeConnection(TransportConnectionContext transportConnectionContext, Session session)
    {
        Sessions.Remove(session);
    }

    internal void DisposeSession(Session session)
    {

    }
}

public class ConnectionSessionContext(LocalPeer localPeer, LocalPeer.Session session) : IConnectionSessionContext
{
    public IEnumerable<ChannelRequest> DialRequests => session.GetRequestQueue();

    public string Id { get; } = Interlocked.Increment(ref Ids.IdCounter).ToString();

    public Remote Remote => session.Remote;

    public void Dispose()
    {
        localPeer.DisposeSession(session);
    }
}

public class SessionContext(LocalPeer localPeer, LocalPeer.Session session, IProtocol protocol) : ContextBase(localPeer, session, protocol), ISessionContext
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

public class TransportConnectionContext(LocalPeer localPeer, LocalPeer.Session session, IProtocol protocol) : ContextBase(localPeer, session, protocol), ITransportConnectionContext
{
    public CancellationToken Token => session.ConnectionToken;

    public IConnectionSessionContext CreateSession()
    {
        return localPeer.CreateSession(session);
    }

    public void Dispose()
    {
        localPeer.DisposeConnection(this, session);
    }
}

public class ConnectionContext(LocalPeer localPeer, LocalPeer.Session session, IProtocol protocol) : ContextBase(localPeer, session, protocol), IConnectionContext
{
    public UpgradeOptions? UpgradeOptions { get; init; }

    public IConnectionSessionContext CreateSession()
    {
        return localPeer.CreateSession(session);
    }

    public Task DisconnectAsync()
    {
        return session.DisconnectAsync();
    }
}

public class ContextBase(LocalPeer localPeer, LocalPeer.Session session, IProtocol protocol) : IChannelFactory
{
    public IPeer Peer => localPeer;
    public Remote Remote => session.Remote;

    public IEnumerable<IProtocol> SubProtocols => localPeer.GetProtocolsFor(protocol);

    public string Id { get; } = Interlocked.Increment(ref Ids.IdCounter).ToString();

    protected LocalPeer localPeer = localPeer;
    protected LocalPeer.Session session = session;
    protected IProtocol protocol = protocol;

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
}
