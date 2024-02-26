// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public interface IPeerFactory
{
    ILocalPeer Create(Identity? identity = default, Multiaddress? localAddr = default);
}
