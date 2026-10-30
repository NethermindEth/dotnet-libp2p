// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core.Context;

public class NewSessionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions,
    ActivitySource? activitySource, Activity? parentActivity, ILoggerFactory? loggerFactory = null)
    : ContextBase(localPeer, session, protocol, isListener, upgradeOptions, activitySource, parentActivity), INewSessionContext
{
    private readonly ILogger? logger = loggerFactory?.CreateLogger<NewSessionContext>();
    protected override bool IsApplicationStream => true;

    public IEnumerable<UpgradeOptions> DialRequests => session.GetRequestQueue();

    public CancellationToken Token => session.ConnectionToken;

    public Task Completion => session.DrainUpgradesAsync();
    public bool TryDisconnectIfIdle(TimeSpan idleTimeout) => session.TryDisconnectIfIdle(idleTimeout);

    public new Activity? Activity { get; } = activitySource?.CreateActivity("New session", ActivityKind.Internal, parentActivity?.Context ?? default);

    public void Dispose()
    {
        logger?.LogDebug("Disposing session context {Id}", Id);
        _ = session.DisconnectAsync();
    }
}
