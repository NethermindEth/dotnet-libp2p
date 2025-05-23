// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Nethermind.Libp2p.Core.Context;

public class SessionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions,
    ActivitySource? activitySource, Activity? parentActivity)
    : ContextBase(localPeer, session, protocol, isListener, upgradeOptions, activitySource, parentActivity), ISessionContext
{
    public UpgradeOptions? UpgradeOptions => upgradeOptions;

    public async Task DialAsync<TProtocol>() where TProtocol : ISessionProtocol
    {
        await session.DialAsync<TProtocol>();
    }

    public async Task DialAsync(ISessionProtocol protocol)
    {
        await session.DialAsync(protocol);
    }

    public Task DisconnectAsync()
    {
        return session.DisconnectAsync();
    }
}
