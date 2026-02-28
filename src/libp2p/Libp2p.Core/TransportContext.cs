// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public class TransportContext(LocalPeer peer, ProtocolRef proto, bool isListener, Activity? activity) : ITransportContext
{
    public Identity Identity => peer.Identity;
    public ILocalPeer Peer => peer;
    public bool IsListener => isListener;
    public Activity? Activity { get; } = activity;

    public void ListenerReady(Multiaddress addr)
    {
        peer.ListenerReady(this, addr);
    }

    public virtual INewConnectionContext CreateConnection()
    {
        return peer.CreateConnection(proto, null, isListener, Activity);
    }
}

public class DialerTransportContext(LocalPeer peer, LocalPeer.Session session, ProtocolRef proto, Activity? context)
    : TransportContext(peer, proto, false, context)
{
    public override INewConnectionContext CreateConnection()
    {
        return peer.CreateConnection(proto, session, false, context);
    }
}
