// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core.Exceptions;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public abstract class PeerFactory : IPeerFactory
{
    private readonly IServiceProvider _serviceProvider;

    private IId _protocol;
    private IChannelFactory _upChannelFactory;
    private static int CtxId = 0;

    public PeerFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public abstract ILocalPeer Create(Identity? identity = default, Multiaddress? localAddr = default);

    /// <summary>
    /// PeerFactory interface ctor
    /// </summary>
    /// <param name="upChannelFactory"></param>
    /// <param name="appFactory"></param>
    /// <param name="protocol"></param>
    /// <param name="appLayerProtocols"></param>
    public void Setup(IId protocol, IChannelFactory upChannelFactory)
    {
        _protocol = protocol;
        _upChannelFactory = upChannelFactory;
    }

    private async Task<IListener> ListenAsync(LocalPeer peer, Multiaddress addr, CancellationToken token)
    {
        peer.Address = addr;
        if (!peer.Address.Has<P2P>())
        {
            peer.Address = peer.Address.Add<P2P>(peer.Identity.PeerId.ToString());
        }

        Channel chan = new();
        if (token != default)
        {
            token.Register(() => chan.CloseAsync());
        }

        TaskCompletionSource ts = new();


        PeerContext peerContext = new()
        {
            Id = $"ctx-{++CtxId}",
            LocalPeer = peer,
        };

        peerContext.OnListenerReady += OnListenerReady;

        void OnListenerReady()
        {
            ts.SetResult();
            peerContext.OnListenerReady -= OnListenerReady;
        }

        RemotePeer remotePeer = new(this, peer, peerContext);
        peerContext.RemotePeer = remotePeer;

        PeerListener result = new(chan, peer);
        peerContext.OnRemotePeerConnection += remotePeer =>
        {
            if (((RemotePeer)remotePeer).LocalPeer != peer)
            {
                return;
            }

            ConnectedTo(remotePeer, false)
                .ContinueWith(t => { result.RaiseOnConnection(remotePeer); }, token);
        };

        _ = _protocol switch
        {
            IProtocol protocol => _ = protocol.ListenAsync(chan, _upChannelFactory, peerContext),
            ITransportProtocol protocol => _ = protocol.ListenAsync(chan, _upChannelFactory, peerContext),
            _ => throw new Libp2pSetupException($"{nameof(IProtocol)} or {nameof(ITransportProtocol)} should be implemented by {_protocol.GetType()}"),
        };        

        await ts.Task;
        return result;
    }

    protected virtual Task ConnectedTo(IRemotePeer peer, bool isDialer)
    {
        return Task.CompletedTask;
    }

    private Task DialAsync<TProtocol>(IPeerContext peerContext, CancellationToken token) where TProtocol : IProtocol
    {
        TaskCompletionSource cts = new(token);
        peerContext.SubDialRequests.Add(new ChannelRequest
        {
            SubProtocol = PeerFactoryBuilderBase.CreateProtocolInstance<TProtocol>(_serviceProvider),
            CompletionSource = cts
        });
        return cts.Task;
    }

    protected virtual async Task<IRemotePeer> DialAsync(LocalPeer peer, Multiaddress addr, CancellationToken token)
    {
        try
        {
            Channel chan = new();
            token.Register(() => _ = chan.CloseAsync());

            PeerContext context = new()
            {
                Id = $"ctx-{++CtxId}",
                LocalPeer = peer,
            };
            RemotePeer result = new(this, peer, context) { Address = addr, Channel = chan };
            context.RemotePeer = result;

            TaskCompletionSource<bool> tcs = new();
            RemotePeerConnected remotePeerConnected = null!;

            remotePeerConnected = remotePeer =>
            {
                if (((RemotePeer)remotePeer).LocalPeer != peer)
                {
                    return;
                }

                ConnectedTo(remotePeer, true).ContinueWith((t) => { tcs.TrySetResult(true); });
                context.OnRemotePeerConnection -= remotePeerConnected;
            };
            context.OnRemotePeerConnection += remotePeerConnected;

            _ = _protocol switch
            {
                IProtocol protocol => _ = protocol.DialAsync(chan, _upChannelFactory, context),
                ITransportProtocol protocol => _ = protocol.DialAsync(chan, _upChannelFactory, context),
                _ => throw new Libp2pSetupException($"{nameof(IProtocol)} or {nameof(ITransportProtocol)} should be implemented by {_protocol.GetType()}"),
            };

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
        public Multiaddress Address => _localPeer.Address;

        public Task DisconnectAsync()
        {
            return _chan.CloseAsync().AsTask();
        }

        public TaskAwaiter GetAwaiter()
        {
            return _chan.GetAwaiter();
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
        public Multiaddress Address { get; set; }

        public Task<IRemotePeer> DialAsync(Multiaddress addr, CancellationToken token = default)
        {
            return _factory.DialAsync(this, addr, token);
        }

        public Task<IListener> ListenAsync(Multiaddress addr, CancellationToken token = default)
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
        public Multiaddress Address { get; set; }
        internal ILocalPeer LocalPeer { get; }

        public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol
        {
            return _factory.DialAsync<TProtocol>(peerContext, token);
        }

        public Task DisconnectAsync()
        {
            return Channel.CloseAsync().AsTask();
        }

        public IPeer Fork()
        {
            return (IPeer)MemberwiseClone();
        }
    }
}

