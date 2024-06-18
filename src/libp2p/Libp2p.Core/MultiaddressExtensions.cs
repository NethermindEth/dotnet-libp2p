// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Core;

public static class MultiaddressExtensions
{
    public static PeerId? GetPeerId(this Multiaddress addr) => addr.Has<P2P>() ? new PeerId(addr.Get<P2P>().ToString()) : default;
}
