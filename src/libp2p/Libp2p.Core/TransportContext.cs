// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public class TransportContext(LocalPeer peer, ProtocolRef proto, bool isListener) : ITransportContext
{
    public string Id { get; } = Interlocked.Increment(ref Ids.IdCounter).ToString();
    public Identity Identity => peer.Identity;
    public IPeer Peer => peer;
    public bool IsListener => isListener;

    public void ListenerReady(Multiaddress addr)
    {
        peer.ListenerReady(this, addr);
    }

    public INewConnectionContext CreateConnection()
    {
        return peer.CreateConnection(proto, isListener);
    }
}
