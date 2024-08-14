// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core.TestsBase;

public class TestLocalPeer : IPeer
{
    public TestLocalPeer()
    {
        Identity = new(Enumerable.Repeat((byte)42, 32).ToArray());
        Address = $"/p2p/{Identity.PeerId}";
    }

    public Identity Identity { get; set; }
    public Multiaddress Address { get; set; }

    public ObservableCollection<Multiaddress> ListenAddresses => throw new NotImplementedException();

    public event OnConnection OnConnection;

    public Task<ISession> DialAsync(Multiaddress addr, CancellationToken token = default)
    {
        return Task.FromResult<ISession>(new TestRemotePeer(addr));
    }

    //public Task<IListener> ListenAsync(Multiaddress addr, CancellationToken token = default)
    //{
    //    return Task.FromResult<IListener>(null);
    //}

    public Task StartListenAsync(Multiaddress[] addrs, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }
}

public class TestRemotePeer : ISession
{
    public TestRemotePeer(Multiaddress addr)
    {
        Identity = TestPeers.Identity(addr);
        Address = addr;
    }

    public Identity Identity { get; set; }
    public Multiaddress Address { get; set; }

    public Multiaddress RemoteAddress => new Multiaddress();

    public Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol
    {
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        return Task.CompletedTask;
    }
}
