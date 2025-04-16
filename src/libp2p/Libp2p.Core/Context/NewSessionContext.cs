// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Core.Context;

public class NewSessionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions, ILoggerFactory? loggerFactory = null) : ContextBase(localPeer, session, protocol, isListener, upgradeOptions), INewSessionContext
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
