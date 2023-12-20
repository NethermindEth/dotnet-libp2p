// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core.TestsBase;

public class TestLocalPeer : ILocalPeer
{
    public TestLocalPeer()
    {
        Identity = new(Enumerable.Repeat((byte)42, 32).ToArray());
        Address = $"/p2p/{Identity.PeerId}";
    }

    public Identity Identity { get; set; }
    public Multiaddress Address { get; set; }

    public Task<IRemotePeer> DialAsync(Multiaddress addr, CancellationToken token = default)
    {
        return Task.FromResult<IRemotePeer>(new TestRemotePeer(addr));
    }

    public Task<ILocalListener> ListenAsync(Multiaddress addr, CancellationToken token = default)
    {
        return Task.FromResult<ILocalListener>(null);
    }
}

public class TestRemotePeer : IRemotePeer
{
    public TestRemotePeer(Multiaddress addr)
    {
        Identity = TestPeers.Identity(addr);
        Address = addr;
    }

    public Identity Identity { get; set; }
    public Multiaddress Address { get; set; }

    public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IId, IDialer
    {
        return Task.CompletedTask;
    }

    public Task<TResult> DialAsync<TProtocol, TResult, TParams>(TParams @params, CancellationToken token = default) where TProtocol : IId, IDialer<TResult, TParams>
    {
        return Task.FromResult(default(TResult)!);
    }

    public Task<TResult> DialAsync<TProtocol, TResult>(CancellationToken token = default) where TProtocol : IId, IDialer<TResult>
    {
        return Task.FromResult(default(TResult)!);
    }

    public Task DisconnectAsync()
    {
        return Task.CompletedTask;
    }
}
