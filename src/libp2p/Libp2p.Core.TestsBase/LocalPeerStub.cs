// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core.TestsBase;

public sealed class LocalPeerStub : ILocalPeer
{
    public LocalPeerStub()
    {
        Identity = new([.. Enumerable.Repeat((byte)42, 32)]);
        Address = $"/p2p/{Identity.PeerId}";
    }

    public Identity Identity { get; set; }
    public Multiaddress Address { get; set; }

    public ObservableCollection<Multiaddress> ListenAddresses => throw new NotImplementedException();

    public event Connected? OnConnected = _ => { };

    public Task<ISession> DialAsync(Multiaddress addr, CancellationToken token = default)
    {
        return Task.FromResult<ISession>(new TestRemotePeer(addr));
    }

    public Task<ISession> DialAsync(Multiaddress[] samePeerAddrs, CancellationToken token = default)
    {
        return Task.FromResult<ISession>(new TestRemotePeer(samePeerAddrs.First()));
    }

    public Task<ISession> DialAsync(PeerId peerId, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public Task StartListenAsync(Multiaddress[]? addrs = null, CancellationToken token = default)
    {
        return Task.CompletedTask;
    }
}

public class TestRemotePeer(Multiaddress addr) : ISession
{
    public Identity Identity { get; set; } = TestPeers.Identity(addr);
    public Multiaddress Address { get; set; } = addr;

    public Multiaddress RemoteAddress => $"/p2p/{Identity.PeerId}";

    public Activity? Activity => throw new NotImplementedException();

    public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol
    {
        return Task.CompletedTask;
    }

    public Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>
    {
        throw new NotImplementedException();
    }

    public Task DisconnectAsync()
    {
        return Task.CompletedTask;
    }
}
