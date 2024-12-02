// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

internal class TestLocalPeer(Identity id) : IPeer
{
    public Identity Identity { get => id; set => throw new NotImplementedException(); }
    public Multiaddress Address { get => $"/p2p/{id.PeerId}"; set => throw new NotImplementedException(); }

    public ObservableCollection<Multiaddress> ListenAddresses => throw new NotImplementedException();

    public event Connected? OnConnected;

    public Task<ISession> DialAsync(Multiaddress[] samePeerAddrs, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    public Task DisconnectAsync()
    {
        throw new NotImplementedException();
    }


    public Task StartListenAsync(Multiaddress[] addrs, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    Task<ISession> IPeer.DialAsync(Multiaddress addr, CancellationToken token)
    {
        throw new NotImplementedException();
    }
}
