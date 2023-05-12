// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IPeerFactory
{
    ILocalPeer Create(Identity? identity = default, MultiAddr? localAddr = default);
}
