// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Stack;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public class PeerFactory(IProtocolStackSettings protocolStackSettings, ILoggerFactory? loggerFactory = null) : IPeerFactory
{
    protected IProtocolStackSettings protocolStackSettings = protocolStackSettings;

    public virtual IPeer Create(Identity? identity = default)
    {
        return new LocalPeer(identity ?? new Identity(), protocolStackSettings, loggerFactory);
    }
}
