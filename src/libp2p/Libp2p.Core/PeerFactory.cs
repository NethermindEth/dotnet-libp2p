// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Core;

public class PeerFactory(IProtocolStackSettings protocolStackSettings, ILoggerFactory? loggerFactory = null) : IPeerFactory
{
    protected IProtocolStackSettings protocolStackSettings = protocolStackSettings;

    public virtual IPeer Create(Identity? identity = default)
    {
        return new LocalPeer(identity ?? new Identity(), protocolStackSettings, loggerFactory);
    }
}
