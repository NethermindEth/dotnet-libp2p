// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public class PeerFactory : IPeerFactory
{
    private readonly ActivitySource? _activitySource;
    protected IProtocolStackSettings protocolStackSettings;
    private static int _factoryCounter;

    public PeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, ActivitySource? activitySource, Activity? rootActivity = null, ILoggerFactory? loggerFactory = null)
    {
        this.activitySource = activitySource;
        string randomTraceId = ActivityTraceId.CreateRandom().ToString();
        string randomParentSpanId = ActivitySpanId.CreateRandom().ToString();
        string parentId = "";//; $"00-{randomTraceId}-{randomParentSpanId}-01"; // Format: Version-TraceId-SpanId-Flags

        this.rootActivity = rootActivity ?? activitySource?.StartActivity($"Factory #{Interlocked.Increment(ref factoryCounter)}", ActivityKind.Internal, parentId);
        this.protocolStackSettings = protocolStackSettings;
        PeerStore = peerStore;
        LoggerFactory = loggerFactory;
    }

    protected PeerStore PeerStore { get; }
    protected ILoggerFactory? LoggerFactory { get; }

    protected Activity? rootActivity;

    public virtual ILocalPeer Create(Identity? identity = default)
    {
        return new LocalPeer(identity ?? new Identity(), PeerStore, protocolStackSettings, activitySource, rootActivity, LoggerFactory);
    }
}
