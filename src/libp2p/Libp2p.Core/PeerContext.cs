// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core;

public class PeerContext : IPeerContext
{
    public string Id { get; set; }
    public IPeer LocalPeer { get; set; }
    public IPeer RemotePeer { get; set; }
    public Multiaddress RemoteEndpoint { get; set; }
    public Multiaddress LocalEndpoint { get; set; }
    internal BlockingCollection<IChannelRequest> SubDialRequests { get; set; } = new();
    public IEnumerable<IChannelRequest> GetBlockingSubDialRequestsEnumerable() => SubDialRequests.GetConsumingEnumerable();
    public IChannelRequest? SpecificProtocolRequest { get; set; }

    public IPeerContext Fork()
    {
        PeerContext result = (PeerContext)MemberwiseClone();
        result.RemotePeer = ((PeerFactory.RemotePeer)RemotePeer).Fork();
        return result;
    }

    public event RemotePeerConnected? OnRemotePeerConnection;
    public void Connected(IPeer peer)
    {
        OnRemotePeerConnection?.Invoke((IRemotePeer)peer);
    }

    public event ListenerReady? OnListenerReady;
    public void ListenerReady()
    {
        OnListenerReady?.Invoke();
    }

    public void RequestDial(IId protocol)
    {
        SubDialRequests.Add(new ChannelRequest { SubProtocol = protocol });
    }
}
