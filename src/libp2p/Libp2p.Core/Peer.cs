// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Stack;
using Org.BouncyCastle.Tls;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core;

public class LocalPeer(IBuilderContext builderContext, Identity identity) : IPeer
{
    public Identity Identity { get; } = identity;

    public ObservableCollection<Multiaddress> ListenAddresses { get; } = new();
    public ObservableCollection<Session> Sessions { get; } = new();

    public class Session(LocalPeer localPeer) : ISession
    {
        public string Id { get; } = Interlocked.Increment(ref Ids.IdCounter).ToString();



        public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol
        {
            TaskCompletionSource tcs = new();
            SubDialRequests.Add(new ChannelRequest() { CompletionSource = tcs, SubProtocol = localPeer.GetProtocolInstance<TProtocol>() });
            return tcs.Task;
        }

        public Task DialAsync(IProtocol[] protocols, CancellationToken token = default)
        {
            TaskCompletionSource tcs = new();
            SubDialRequests.Add(new ChannelRequest() { CompletionSource = tcs, SubProtocol = protocols[0] });
            return tcs.Task;
        }

        private CancellationTokenSource connectionTokenSource = new();

        public CancellationToken ConnectionToken => connectionTokenSource.Token;


        private TaskCompletionSource ConnectedTcs = new();

        public Task Connected => ConnectedTcs.Task;

        public PeerId PeerId { get; set; }

        private BlockingCollection<IChannelRequest> SubDialRequests = new();

        internal IEnumerable<IChannelRequest> GetRequestQueue() => SubDialRequests.GetConsumingEnumerable(ConnectionToken);

        internal Task DisconnectAsync()
        {
            return Task.CompletedTask;
        }
    }

    protected virtual IProtocol SelectProtocol(Multiaddress addr)
    {
        if (builderContext.TopProtocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(builderContext)}");
        }

        if (builderContext.TopProtocols.Length is not 1)
        {
            throw new Libp2pSetupException("Top protocol should be single one by default");

        }

        return builderContext.TopProtocols.Single();
    }

    Dictionary<ITransportContext, TaskCompletionSource<Multiaddress>> listenerReadyTcs = new();

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

            ITransportContext ctx = new TransportContext(this, transportProtocol);
            TaskCompletionSource<Multiaddress> tcs = new();
            listenerReadyTcs[ctx] = tcs;

            _ = transportProtocol.ListenAsync(ctx, addr, token).ContinueWith(t => ListenAddresses.Remove(tcs.Task.Result));

            listenTasks.Add(tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(5000)));
            ListenAddresses.Add(tcs.Task.Result);
        }

        await Task.WhenAll(listenTasks);
    }

    public ITransportContext CreateContext(ITransportProtocol tProto)
    {
        return new TransportContext(this, tProto);
    }

    public void ListenerReady(ITransportContext sender, Multiaddress addr)
    {
        if (listenerReadyTcs.Remove(sender, out TaskCompletionSource<Multiaddress>? tcs))
        {
            tcs.SetResult(addr);
        }
    }

    public ITransportConnectionContext CreateConnection(ITransportProtocol proto)
    {
        Session session = new(this);
        Sessions.Add(session);
        return new TransportConnectionContext(this, session, proto);
    }

    public IConnectionSessionContext CreateSession(Session session, PeerId peerId)
    {
        lock (Sessions)
        {
            if(Sessions.Any(s => !object.ReferenceEquals(session, s) && s.PeerId == peerId))
            {
                throw new Libp2pException("Session is already established");
            }

            session.PeerId = peerId;
            return new ConnectionSessionContext(this, session);
        }
    }

    internal IEnumerable<IProtocol> GetProtocolsFor(IProtocol protocol)
    {
        if (builderContext.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(builderContext)}");
        }

        if (!builderContext.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is noty added");
        }

        return builderContext.Protocols[protocol];
    }

    internal IProtocol? GetProtocolInstance<TProtocol>()
    {
        return builderContext.Protocols?.Keys.FirstOrDefault(p => p.GetType() == typeof(TProtocol));
    }

    public async Task<ISession> DialAsync(Multiaddress addr, CancellationToken token = default)
    {
        IProtocol dialerProtocol = SelectProtocol(addr);

        if (dialerProtocol is not ITransportProtocol transportProtocol)
        {
            throw new Libp2pSetupException($"{nameof(ITransportProtocol)} should be implemented by {dialerProtocol.GetType()}");
        }

        Session session = new(this);
        ITransportConnectionContext ctx = new TransportConnectionContext(this, session, transportProtocol);

        _ = transportProtocol.DialAsync(ctx, addr, token);

        await session.Connected;
        return session;
    }

    internal IChannel SubDial(LocalPeer localPeer, Session session, IProtocol protocol, IChannelRequest? request)
    {
        if (builderContext.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(builderContext)}");
        }

        if (!builderContext.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is noty added");
        }

        IProtocol top = request?.SubProtocol ?? builderContext.Protocols[protocol].First();

        Channel res = new Channel();
        if (top is IConnectionProtocol tProto)
        {
            var ctx = new ConnectionContext(this, session, top);
            _ = tProto.DialAsync(res.Reverse, ctx);
        }
        if (top is ISessionProtocol sProto)
        {
            var ctx = new SessionContext(this, session, top);
            _ = sProto.DialAsync(res.Reverse, ctx);
        }
        return res;
    }

    internal IChannel SubListen(LocalPeer localPeer, Session session, IProtocol protocol, IChannelRequest? request)
    {
        if (builderContext.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(builderContext)}");
        }

        if (!builderContext.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is noty added");
        }

        IProtocol top = request?.SubProtocol ?? builderContext.Protocols[protocol].First();

        Channel res = new Channel();
        if (top is IConnectionProtocol tProto)
        {
            var ctx = new ConnectionContext(this, session, top);
            _ = tProto.ListenAsync(res.Reverse, ctx);
        }
        if (top is ISessionProtocol sProto)
        {
            var ctx = new SessionContext(this, session, top);
            _ = sProto.ListenAsync(res.Reverse, ctx);
        }
        return res;
    }

    internal async Task SubDialAndBind(LocalPeer localPeer, Session session, IChannel parentChannel, IProtocol protocol, IChannelRequest? request)
    {
        if (builderContext.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(builderContext)}");
        }

        if (!builderContext.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is noty added");
        }

        IProtocol top = request?.SubProtocol ?? builderContext.Protocols[protocol].First();

        if (top is IConnectionProtocol tProto)
        {
            var ctx = new ConnectionContext(this, session, top);
            await tProto.DialAsync(parentChannel, ctx);
        }
        if (top is ISessionProtocol sProto)
        {
            var ctx = new SessionContext(this, session, top);
            await sProto.DialAsync(parentChannel, ctx);
        }
    }


    internal async Task SubListenAndBind(LocalPeer localPeer, Session session, IChannel parentChannel, IProtocol protocol, IChannelRequest? request)
    {
        if (builderContext.Protocols is null)
        {
            throw new Libp2pSetupException($"Protocols are not set in {nameof(builderContext)}");
        }

        if (!builderContext.Protocols.ContainsKey(protocol))
        {
            throw new Libp2pSetupException($"{protocol} is noty added");
        }

        IProtocol top = request?.SubProtocol ?? builderContext.Protocols[protocol].First();

        if (top is IConnectionProtocol tProto)
        {
            var ctx = new ConnectionContext(this, session, top);
            await tProto.ListenAsync(parentChannel, ctx);
        }
        if (top is ISessionProtocol sProto)
        {
            var ctx = new SessionContext(this, session, top);
            await sProto.ListenAsync(parentChannel, ctx);
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
    public IEnumerable<IChannelRequest> DialRequests => session.GetRequestQueue();

    public string Id { get; } = Interlocked.Increment(ref Ids.IdCounter).ToString();

    public void Dispose()
    {
        localPeer.DisposeSession(session);
    }
}

public class SessionContext(LocalPeer localPeer, LocalPeer.Session session, IProtocol protocol) : ContextBase(localPeer, session, protocol), ISessionContext
{
    public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol
    {
        return session.DialAsync<TProtocol>(token);
    }

    public Task DisconnectAsync()
    {
        return session.DisconnectAsync();
    }
}

public class TransportConnectionContext(LocalPeer localPeer, LocalPeer.Session session, IProtocol protocol) : ContextBase(localPeer, session, protocol), ITransportConnectionContext
{
    public CancellationToken Token => session.ConnectionToken;

    public IConnectionSessionContext CreateSession(PeerId peerId)
    {
        return localPeer.CreateSession(session, peerId);
    }

    public void Dispose()
    {
        localPeer.DisposeConnection(this, session);
    }
}

public class ConnectionContext(LocalPeer localPeer, LocalPeer.Session session, IProtocol protocol) : ContextBase(localPeer, session, protocol), IConnectionContext
{
    public IConnectionSessionContext CreateSession(PeerId peerId)
    {
        return localPeer.CreateSession(session, peerId);
    }

    public Task DisconnectAsync()
    {
        return session.DisconnectAsync();
    }
}


public class ContextBase(LocalPeer localPeer, LocalPeer.Session session, IProtocol protocol)
{
    public IPeer Peer => localPeer;

    public IEnumerable<IProtocol> SubProtocols => localPeer.GetProtocolsFor(protocol);

    public string Id { get; } = Interlocked.Increment(ref Ids.IdCounter).ToString();

    protected LocalPeer localPeer = localPeer;
    protected LocalPeer.Session session = session;
    protected IProtocol protocol = protocol;

    public IChannel SubDial(IChannelRequest? request = null)
    {
        return localPeer.SubDial(localPeer, session, protocol, request);
    }

    public Task SubDialAndBind(IChannel parentChannel, IChannelRequest? request = null)
    {
        return localPeer.SubDialAndBind(localPeer, session, parentChannel, protocol, request);
    }

    public IChannel SubListen(IChannelRequest? request = null)
    {
        return localPeer.SubListen(localPeer, session, protocol, request);
    }

    public Task SubListenAndBind(IChannel parentChannel, IChannelRequest? request = null)
    {
        return localPeer.SubListenAndBind(localPeer, session, parentChannel, protocol, request);
    }
}
