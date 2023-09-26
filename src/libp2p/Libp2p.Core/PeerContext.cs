// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core;

public class PeerContext : IPeerContext
{
    public string Id { get; set; }
    public IPeer LocalPeer { get; set; }
    public IPeer RemotePeer { get; set; }
    public Multiaddr RemoteEndpoint { get; set; }
    public Multiaddr LocalEndpoint { get; set; }
    public BlockingCollection<IChannelNegotiationRequest> DialRequests { get; set; } = new();

    public event RemotePeerConnected? OnRemotePeerConnection;
    public IChannelNegotiationRequest? SpecificProtocolRequest { get; set; }

    public IPeerContext Fork()
    {
        PeerContext result = (PeerContext)MemberwiseClone();
        result.RemotePeer = ((PeerFactory.RemotePeer)RemotePeer).Fork();
        return result;
    }

    public void Connected(IPeer peer)
    {
        OnRemotePeerConnection?.Invoke((IRemotePeer)peer);
    }
}
