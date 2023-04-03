// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Nethermind.Libp2p.Core.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

public class PeerFactory : IPeerFactory
{
    private readonly IServiceProvider _serviceProvider;
    private IChannelFactory _appFactory;
    private IProtocol _connectionProtocol;
    private IChannelFactory _rootFactory;
    private IEnumerable<IProtocol> _appLayerProtocols;

    protected PeerFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public ILocalPeer Create(Identity? identity = default)
    {
        return new LocalPeer(this) { Identity = identity ?? new Identity() };
    }

    public void Connect(IChannelFactory rootFactory, IChannelFactory appFactory, IProtocol connectionProtocol, IEnumerable<IProtocol> appLayerProtocols)
    {
        _rootFactory = rootFactory;
        _appFactory = appFactory;
        _connectionProtocol = connectionProtocol;
        _appLayerProtocols = appLayerProtocols;
    }

    //public event OnConnection? OnConnection;

    private Task DialAsync<TProtocol>(CancellationToken token) where TProtocol : IProtocol
    {
        TaskCompletionSource<bool> cts = new(token);
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
        if (token != default)
        {
            token.Register(() => chan.CloseAsync());
        }

        PeerContext peerCtx = new()
        {
            LocalPeer = peer,
            RemotePeer = new RemotePeer(this, peer),
            ApplayerProtocols = _appLayerProtocols
        };
        PeerListener result = new(chan, peer);
        
        _appFactory.OnRemotePeerConnection += remotePeer =>
        {
            if (((RemotePeer)remotePeer).LocalPeer != peer)
            {
                return;
            }

            ConnectedTo(remotePeer, false)
                .ContinueWith(t => { result.RaiseOnConnection(remotePeer); }, token);
        };
        _ = _connectionProtocol.ListenAsync(chan, _rootFactory, peerCtx);

        return result;
    }

    protected virtual Task ConnectedTo(IRemotePeer peer, bool isDialer)
    {
        return Task.CompletedTask;
    }

    private async Task<IRemotePeer> DialAsync(LocalPeer peer, MultiAddr addr, CancellationToken token)
    {
        RemotePeer result = new(this, peer) { Address = addr };
        Channel chan = new();
        _ = _connectionProtocol.DialAsync(chan, _rootFactory,
            new PeerContext
            {
                LocalPeer = peer, 
                RemotePeer = result,
                ApplayerProtocols = _appLayerProtocols
            });
        result.Channel = chan;
        var tcs = new TaskCompletionSource<bool>();
        _appFactory.OnRemotePeerConnection += remotePeer =>
        {
            if (((RemotePeer)remotePeer).LocalPeer != peer)
            {
                return;
            }
            ConnectedTo(remotePeer, true).ContinueWith((t) =>
            {
                tcs.SetResult(true);
            }); 
        };
        await tcs.Task;
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

    internal class RemotePeer : IRemotePeer
    {
        private readonly PeerFactory _factory;

        public RemotePeer(PeerFactory factory, ILocalPeer localPeer)
        {
            _factory = factory;
            LocalPeer = localPeer;
        }

        public Channel Channel { get; set; }

        public Identity Identity { get; set; }
        public MultiAddr Address { get; set; }
        internal ILocalPeer LocalPeer { get; }

        public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol
        {
            return _factory.DialAsync<TProtocol>(token);
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
