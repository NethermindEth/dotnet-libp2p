// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;

namespace Nethermind.Libp2p.Core;

public class PeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, ILoggerFactory? loggerFactory = null) : IPeerFactory
{
    protected IProtocolStackSettings protocolStackSettings = protocolStackSettings;

    protected PeerStore PeerStore { get; } = peerStore;
    protected ILoggerFactory? LoggerFactory { get; } = loggerFactory;

    public virtual ILocalPeer Create(Identity? identity = default)
    {
        return new LocalPeer(identity ?? new Identity(), PeerStore, protocolStackSettings, LoggerFactory);
    }
}
