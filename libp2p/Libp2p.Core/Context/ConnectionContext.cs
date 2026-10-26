// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Nethermind.Libp2p.Core.Context;

public class ConnectionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions,
    ActivitySource? activitySource, Activity? activity)
    : ContextBase(localPeer, session, protocol, isListener, upgradeOptions, activitySource, activity), IConnectionContext
{
    public UpgradeOptions? UpgradeOptions => upgradeOptions;

    public Task DisconnectAsync()
    {
        return session.DisconnectAsync();
    }
}
