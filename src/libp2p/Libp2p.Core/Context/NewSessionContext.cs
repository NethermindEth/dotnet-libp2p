// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Context;

public class NewSessionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions) : ContextBase(localPeer, session, protocol, isListener, upgradeOptions), INewSessionContext
{
    public IEnumerable<UpgradeOptions> DialRequests => session.GetRequestQueue();

    public CancellationToken Token => session.ConnectionToken;

    public void Dispose()
    {

    }
}
