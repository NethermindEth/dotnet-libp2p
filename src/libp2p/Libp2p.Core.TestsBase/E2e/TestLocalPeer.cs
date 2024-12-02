// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

internal class TestLocalPeer(Identity id) : ILocalPeer
{
    public Identity Identity { get => id; set => throw new NotImplementedException(); }
    public Multiaddress Address { get => $"/p2p/{id.PeerId}"; set => throw new NotImplementedException(); }

    public Task<IRemotePeer> DialAsync(Multiaddress addr, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    public Task<IListener> ListenAsync(Multiaddress addr, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }
}
