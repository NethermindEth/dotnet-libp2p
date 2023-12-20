// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public class PeerFactory : IPeerFactory
{
    private readonly IServiceProvider _serviceProvider;

    private IId _protocol;
    private IChannelFactory _upChannelFactory;
    private static int CtxId = 0;

    public PeerFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public virtual ILocalPeer Create(Identity? identity = default, Multiaddress? localAddr = default)
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
    public void Setup(IId protocol, IChannelFactory upChannelFactory)
    {
        _protocol = protocol;
        _upChannelFactory = upChannelFactory;
    }

    private async Task<ILocalListener> ListenAsync(LocalPeer peer, Multiaddress addr, CancellationToken token)
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
            try
            {
                ConnectedTo(remotePeer, false)
                    .ContinueWith(t => { result.RaiseOnConnection(remotePeer); }, token);
            }
            catch
            {

            }
        };
        _ = ((IListener)_protocol).ListenAsync(chan, _upChannelFactory, peerContext);

        await ts.Task;
        return result;
    }

    protected virtual Task ConnectedTo(IRemotePeer peer, bool isDialer)
    {
        return Task.CompletedTask;
    }

    private Task DialAsync<TProtocol>(IPeerContext peerContext, CancellationToken token) where TProtocol : IId
    {
        TaskCompletionSource cts = new(token);
        ((PeerContext)peerContext).SubDialRequests.Add(new ChannelRequest
        {
            SubProtocol = PeerFactoryBuilderBase.CreateProtocolInstance<TProtocol>(_serviceProvider),
            SetResult = (result) => cts.SetResult(),
        });
        return cts.Task;
    }

    private Task<TResult> DialAsync<TProtocol, TResult>(IPeerContext peerContext, CancellationToken token)
        where TProtocol : IId, IDialer<TResult>
    {
        TaskCompletionSource<TResult> cts = new(token);
        ((PeerContext)peerContext).SubDialRequests.Add(new ChannelRequest
        {
            SubProtocol = PeerFactoryBuilderBase.CreateProtocolInstance<TProtocol>(_serviceProvider),
            Dial = (subProtocol, downChannel, channelFactory, context)
                => ((IDialer<TResult>)subProtocol).DialAsync(downChannel, channelFactory, context),
            SetResult = (result) => cts.SetResult(((Task<TResult>)result).Result),
        });
        return cts.Task;
    }

    private Task<TResult> DialAsync<TProtocol, TResult, TParams>(TParams @params, IPeerContext peerContext, CancellationToken token)
        where TProtocol : IId, IDialer<TResult, TParams>
    {
        TaskCompletionSource<TResult> cts = new(token);
        ((PeerContext)peerContext).SubDialRequests.Add(new ChannelRequest
        {
            SubProtocol = PeerFactoryBuilderBase.CreateProtocolInstance<TProtocol>(_serviceProvider),
            Dial = (subProtocol, downChannel, channelFactory, context)
                => ((IDialer<TResult, TParams>)subProtocol).DialAsync(downChannel, channelFactory, context, @params),
            SetResult = (result) => cts.SetResult(((Task<TResult>)result).Result),
        });
        return cts.Task;
    }

    protected virtual async Task<IRemotePeer> DialAsync(LocalPeer peer, Multiaddress addr, CancellationToken token)
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

        _ = ((IDialer)_protocol).DialAsync(chan, _upChannelFactory, context);

        await tcs.Task;
        return result;
    }

    private class PeerListener : ILocalListener
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
        public Multiaddress Address { get; set; }

        public Task<IRemotePeer> DialAsync(Multiaddress addr, CancellationToken token = default)
        {
            return _factory.DialAsync(this, addr, token);
        }

        public Task<ILocalListener> ListenAsync(Multiaddress addr, CancellationToken token = default)
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

        public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IId, IDialer
        {
            return _factory.DialAsync<TProtocol>(peerContext, token);
        }

        public Task<TResult> DialAsync<TProtocol, TResult>(CancellationToken token = default)
            where TProtocol : IId, IDialer<TResult>
        {
            return _factory.DialAsync<TProtocol, TResult>(peerContext, token);
        }

        public Task<TResult> DialAsync<TProtocol, TResult, TParams>(TParams @params, CancellationToken token = default)
            where TProtocol : IId, IDialer<TResult, TParams>
        {
            return _factory.DialAsync<TProtocol, TResult, TParams>(@params, peerContext, token);
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
