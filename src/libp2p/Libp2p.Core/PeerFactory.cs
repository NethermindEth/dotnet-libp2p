using System.Runtime.CompilerServices;
using Libp2p.Core.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Libp2p.Core;

public class PeerFactory : IPeerFactory
{
    private readonly IServiceProvider _serviceProvider;
    private IChannelFactory _appFactory;
    private IProtocol _connectionProtocol;
    private IChannelFactory _rootFactory;

    protected PeerFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public ILocalPeer Create(Identity? identity = default)
    {
        return new LocalPeer(this) { Identity = identity ?? new Identity() };
    }

    public void Connect(IChannelFactory rootFactory, IChannelFactory appFactory, IProtocol connectionProtocol)
    {
        _rootFactory = rootFactory;
        _appFactory = appFactory;
        _connectionProtocol = connectionProtocol;
    }

    public event OnConnection? OnConnection;

    private Task DialAsync<TProtocol>(CancellationToken token) where TProtocol : IProtocol
    {
        TaskCompletionSource cts = new(token);
        _appFactory.SubDialRequests.Add(new ChannelRequest
            { SubProtocol = ActivatorUtilities.CreateInstance<TProtocol>(_serviceProvider), CompletionSource = cts });
        return cts.Task;
    }

    private async Task<IListener> ListenAsync(LocalPeer peer, MultiAddr addr, CancellationToken token)
    {
        peer.Address = addr;
        if (!peer.Address.Has(Multiaddr.P2p))
        {
            peer.Address = peer.Address.Append(Multiaddr.P2p, peer.Identity.PeerId);
        }

        Channel chan = new();
        token.Register(() => chan.CloseAsync());
        PeerContext peerCtx = new()
        {
            LocalPeer = peer
        };
        PeerListener result = new(chan, peer);
        _appFactory.OnRemotePeerConnection += peer =>
        {
            ConnectedTo(peer, false).ContinueWith(t => { result.RaiseOnConnection(peer); }, token);
        };
        await _connectionProtocol.ListenAsync(chan, _rootFactory, peerCtx);

        return result;
    }

    protected virtual Task ConnectedTo(IRemotePeer peer, bool isDialer)
    {
        return Task.CompletedTask;
    }

    private async Task<IRemotePeer> DialAsync(LocalPeer peer, MultiAddr addr, CancellationToken token)
    {
        RemotePeer result = new(this) { Address = addr };
        Channel chan = new();
        _ = _connectionProtocol.DialAsync(chan, _rootFactory,
            new PeerContext { LocalPeer = peer, RemotePeer = result });
        result.Channel = chan;
        _appFactory.OnRemotePeerConnection += peer => { ConnectedTo(peer, true); };
        return result;
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
        public MultiAddr Address => _localPeer.Address;

        public Task DisconectAsync()
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

    private class LocalPeer : ILocalPeer
    {
        private readonly PeerFactory _factory;

        public LocalPeer(PeerFactory factory)
        {
            _factory = factory;
        }

        public Identity Identity { get; set; }
        public MultiAddr Address { get; set; }

        public Task<IRemotePeer> DialAsync(MultiAddr addr, CancellationToken token = default)
        {
            return _factory.DialAsync(this, addr, token);
        }

        public Task<IListener> ListenAsync(MultiAddr addr, CancellationToken token = default)
        {
            return _factory.ListenAsync(this, addr, token);
        }
    }

    private class RemotePeer : IRemotePeer
    {
        private readonly PeerFactory _factory;

        public RemotePeer(PeerFactory factory)
        {
            _factory = factory;
        }

        public Channel Channel { get; set; }

        public Identity Identity { get; set; }
        public MultiAddr Address { get; set; }

        public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol
        {
            return _factory.DialAsync<TProtocol>(token);
        }

        public Task DisconectAsync()
        {
            return Channel.CloseAsync();
        }
    }
}
