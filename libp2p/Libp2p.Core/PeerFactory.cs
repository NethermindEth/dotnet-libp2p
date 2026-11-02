// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public class PeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore? peerStore = null, ActivitySource? activitySource = null, Activity? rootActivity = null, ILoggerFactory? loggerFactory = null, PeerResourceLimits? resourceLimits = null, TimeProvider? timeProvider = null) : IPeerFactory
{
    public PeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore? peerStore, ActivitySource? activitySource, Activity? rootActivity, ILoggerFactory? loggerFactory)
        : this(protocolStackSettings, peerStore, activitySource, rootActivity, loggerFactory, null, null) { }

    protected PeerResourceLimits ResourceLimits { get; } = resourceLimits ?? PeerResourceLimits.Default;
    protected TimeProvider Clock { get; } = timeProvider ?? TimeProvider.System;

    protected readonly ActivitySource? activitySource = activitySource;
    protected IProtocolStackSettings protocolStackSettings = protocolStackSettings;
    private static int _factoryCounter;

    protected PeerStore PeerStore { get; } = peerStore ?? new PeerStore();
    protected ILoggerFactory? LoggerFactory { get; } = loggerFactory;

    protected Activity? rootActivity = rootActivity ?? activitySource?.StartActivity($"Factory #{Interlocked.Increment(ref _factoryCounter)}", ActivityKind.Internal, "");

    public virtual ILocalPeer Create(Identity? identity = default)
    {
        return new LocalPeer(identity ?? new Identity(), PeerStore, protocolStackSettings, activitySource, rootActivity, LoggerFactory, resourceLimits: ResourceLimits, timeProvider: Clock);
    }
}
