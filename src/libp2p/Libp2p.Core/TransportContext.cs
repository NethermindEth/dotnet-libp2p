// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public class TransportContext(LocalPeer peer, ITransportProtocol proto) : ITransportContext
{
    public string Id { get; } = Interlocked.Increment(ref Ids.IdCounter).ToString();
    public Identity Identity => peer.Identity;
    public IPeer Peer => peer;
    public IRemotePeer RemotePeer => throw new NotImplementedException();

    public void ListenerReady(Multiaddress addr)
    {
        peer.ListenerReady(this, addr);
    }

    public ITransportConnectionContext CreateConnection()
    {
        return peer.CreateConnection(proto);
    }
}
