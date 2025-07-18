// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public class PeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, ActivitySource? activitySource = null, Activity? rootActivity = null, ILoggerFactory? loggerFactory = null) : IPeerFactory
{
    protected readonly ActivitySource? activitySource = activitySource;
    protected IProtocolStackSettings protocolStackSettings = protocolStackSettings;
    private static int _factoryCounter;

    protected PeerStore PeerStore { get; } = peerStore;
    protected ILoggerFactory? LoggerFactory { get; } = loggerFactory;

    protected Activity? rootActivity = rootActivity ?? activitySource?.StartActivity($"Factory #{Interlocked.Increment(ref _factoryCounter)}", ActivityKind.Internal, "");

    public virtual ILocalPeer Create(Identity? identity = default)
    {
        return new LocalPeer(identity ?? new Identity(), PeerStore, protocolStackSettings, activitySource, rootActivity, LoggerFactory);
    }
}
