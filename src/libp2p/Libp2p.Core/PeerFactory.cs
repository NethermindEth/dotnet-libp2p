// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Core;

public class PeerFactory(IBuilderContext builderContext) : IPeerFactory
{
    public virtual IPeer Create(Identity? identity = default)
    {
        return new LocalPeer(builderContext, identity ?? new Identity());
    }
}
