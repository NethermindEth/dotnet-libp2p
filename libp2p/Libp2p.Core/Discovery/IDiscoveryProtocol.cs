// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core.Discovery;

public interface IDiscoveryProtocol
{
    Task StartDiscoveryAsync(IReadOnlyList<Multiaddress> localPeerAddr, CancellationToken token = default);
}
