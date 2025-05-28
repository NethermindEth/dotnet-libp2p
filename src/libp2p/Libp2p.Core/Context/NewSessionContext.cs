// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core.Context;

public sealed class NewSessionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions,
    ActivitySource? activitySource, Activity? parentActivity, ILoggerFactory? loggerFactory = null)
    : ContextBase(localPeer, session, protocol, isListener, upgradeOptions, activitySource,
        activitySource?.CreateActivity("New session", ActivityKind.Internal, parentActivity?.Context ?? default)), INewSessionContext
{
    private readonly ILogger? logger = loggerFactory?.CreateLogger<NewSessionContext>();

    public IEnumerable<UpgradeOptions> DialRequests => session.GetRequestQueue();

    public CancellationToken Token => session.ConnectionToken;

    public void Dispose()
    {
        logger?.LogDebug("Disposing session context {Id}", Id);
        _ = session.DisconnectAsync();
    }
}
