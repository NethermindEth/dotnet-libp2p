// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public class TransportContext(LocalPeer peer, ProtocolRef proto, bool isListener) : ITransportContext
{
    public Identity Identity => peer.Identity;
    public ILocalPeer Peer => peer;
    public bool IsListener => isListener;

    public void ListenerReady(Multiaddress addr)
    {
        peer.ListenerReady(this, addr);
    }

    public virtual INewConnectionContext CreateConnection()
    {
        return peer.CreateConnection(proto, null, isListener);
    }
}

public class DialerTransportContext(LocalPeer peer, LocalPeer.Session session, ProtocolRef proto) : TransportContext(peer, proto, false)
{
    public override INewConnectionContext CreateConnection()
    {
        return peer.CreateConnection(proto, session, false);
    }
}
