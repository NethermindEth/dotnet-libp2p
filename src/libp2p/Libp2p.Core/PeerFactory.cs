// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public class PeerFactory : IPeerFactory
{
    private readonly IServiceProvider _serviceProvider;

    private IProtocol _protocol;
    private IChannelFactory _upChannelFactory;
    private static int CtxId = 0;

    public PeerFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public virtual ILocalPeer Create(Identity? identity = default, Multiaddr? localAddr = default)
    {
        identity ??= new Identity();
        return new LocalPeer(this) { Identity = identity, Address = localAddr ?? $"/ip4/127.0.0.1/tcp/0/p2p/{identity.PeerId}" };
    }

    /// <summary>
    /// PeerFactory interface ctor
    /// </summary>
    /// <param name="upChannelFactory"></param>
    /// <param name="appFactory"></param>
    /// <param name="protocol"></param>
    /// <param name="appLayerProtocols"></param>
    public void Setup(IProtocol protocol, IChannelFactory upChannelFactory)
    {
        _protocol = protocol;
        _upChannelFactory = upChannelFactory;
    }

    private Task<IListener> ListenAsync(LocalPeer peer, Multiaddr addr, CancellationToken token)
    {
        peer.Address = addr;
        if (!peer.Address.Has(Enums.Multiaddr.P2p))
        {
            peer.Address = peer.Address.Append(Enums.Multiaddr.P2p, peer.Identity.PeerId.ToString());
        }

        Channel chan = new();
        if (token != default)
        {
            token.Register(() => chan.CloseAsync());
        }

        PeerContext peerCtx = new()
        {
            Id = $"ctx-{++CtxId}",
            LocalPeer = peer,
        };
        RemotePeer remotePeer = new(this, peer, peerCtx);
        peerCtx.RemotePeer = remotePeer;

        PeerListener result = new(chan, peer);
        peerCtx.OnRemotePeerConnection += remotePeer =>
        {
            if (((RemotePeer)remotePeer).LocalPeer != peer)
            {
                return;
            }

            ConnectedTo(remotePeer, false)
                .ContinueWith(t => { result.RaiseOnConnection(remotePeer); }, token);
        };
        _ = _protocol.ListenAsync(chan, _upChannelFactory, peerCtx);

        return Task.FromResult((IListener)result);
    }

    protected virtual Task ConnectedTo(IRemotePeer peer, bool isDialer)
    {
        return Task.CompletedTask;
    }

    private Task DialAsync<TProtocol>(IPeerContext peerContext, CancellationToken token) where TProtocol : IProtocol
    {
        TaskCompletionSource cts = new(token);
        peerContext.SubDialRequests.Add(new ChannelRequest
        { SubProtocol = PeerFactoryBuilderBase.CreateProtocolInstance<TProtocol>(_serviceProvider), CompletionSource = cts });
        return cts.Task;
    }

    protected virtual async Task<IRemotePeer> DialAsync(LocalPeer peer, Multiaddr addr, CancellationToken token)
    {
        try
        {
            Channel chan = new();
            token.Register(() => chan.CloseAsync());

            PeerContext context = new()
            {
                Id = $"ctx-{++CtxId}",
                LocalPeer = peer,
            };
            RemotePeer result = new(this, peer, context) { Address = addr, Channel = chan };
            context.RemotePeer = result;

            TaskCompletionSource<bool> tcs = new();
            context.OnRemotePeerConnection += remotePeer =>
            {
                if (((RemotePeer)remotePeer).LocalPeer != peer)
                {
                    return;
                }

                ConnectedTo(remotePeer, true).ContinueWith((t) => { tcs.TrySetResult(true); });
            };

            _ = _protocol.DialAsync(chan, _upChannelFactory, context);

            await tcs.Task;
            return result;
        }
        catch
        {
            throw;
        }
    }

    private class PeerListener : IListener
    {
        private readonly Channel _chan;
        private readonly LocalPeer _localPeer;

        public PeerListener(Channel chan, LocalPeer localPeer)
        {
            _chan = chan;
            _localPeer = localPeer;
        }

        public event OnConnection? OnConnection;
        public Multiaddr Address => _localPeer.Address;

        public Task DisconnectAsync()
        {
            return _chan.CloseAsync();
        }

        public TaskAwaiter GetAwaiter()
        {
            return Task.Delay(-1, _chan.Token).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnCanceled)
                .GetAwaiter();
        }

        internal void RaiseOnConnection(IRemotePeer peer)
        {
            OnConnection?.Invoke(peer);
        }
    }

    protected class LocalPeer : ILocalPeer
    {
        private readonly PeerFactory _factory;

        public LocalPeer(PeerFactory factory)
        {
            _factory = factory;
        }

        public Identity? Identity { get; set; }
        public Multiaddr Address { get; set; }

        public Task<IRemotePeer> DialAsync(Multiaddr addr, CancellationToken token = default)
        {
            return _factory.DialAsync(this, addr, token);
        }

        public Task<IListener> ListenAsync(Multiaddr addr, CancellationToken token = default)
        {
            return _factory.ListenAsync(this, addr, token);
        }
    }

    internal class RemotePeer : IRemotePeer
    {
        private readonly PeerFactory _factory;
        private readonly IPeerContext peerContext;

        public RemotePeer(PeerFactory factory, ILocalPeer localPeer, IPeerContext peerContext)
        {
            _factory = factory;
            LocalPeer = localPeer;
            this.peerContext = peerContext;
        }

        public Channel Channel { get; set; }

        public Identity Identity { get; set; }
        public Multiaddr Address { get; set; }
        internal ILocalPeer LocalPeer { get; }

        public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol
        {
            return _factory.DialAsync<TProtocol>(peerContext, token);
        }

        public Task DisconnectAsync()
        {
            return Channel.CloseAsync();
        }

        public IPeer Fork()
        {
            return (IPeer)MemberwiseClone();
        }
    }
}
