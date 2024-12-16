// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Context;

public class ConnectionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions) : ContextBase(localPeer, session, protocol, isListener, upgradeOptions), IConnectionContext
{
    public UpgradeOptions? UpgradeOptions => upgradeOptions;

    public Task DisconnectAsync()
    {
        return session.DisconnectAsync();
    }
}
