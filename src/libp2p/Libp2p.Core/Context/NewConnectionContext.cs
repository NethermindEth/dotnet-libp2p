// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Nethermind.Libp2p.Core.Context;

public class NewConnectionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions,
    ActivitySource? activitySource, Activity? parentActivity)
    : ContextBase(localPeer, session, protocol, isListener, upgradeOptions, activitySource, parentActivity), INewConnectionContext
{
    public CancellationToken Token { get; } = session.ConnectionToken;

    public void Dispose()
    {
        Activity?.Dispose();
    }
}
