// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Core;

public class PeerFactory(IProtocolStackSettings protocolStackSettings) : IPeerFactory
{
    protected IProtocolStackSettings protocolStackSettings = protocolStackSettings;

    public virtual IPeer Create(Identity? identity = default)
    {
        return new LocalPeer(protocolStackSettings, identity ?? new Identity());
    }
}
