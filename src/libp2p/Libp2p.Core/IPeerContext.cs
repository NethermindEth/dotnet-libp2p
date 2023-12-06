// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core;

public interface IPeerContext
{
    string Id { get; }
    IPeer LocalPeer { get; }
    IPeer RemotePeer { get; }

    Multiaddress RemoteEndpoint { get; set; }
    Multiaddress LocalEndpoint { get; set; }

    // TODO: Get rid of this:
    IPeerContext Fork();

    #region Allows muxer to manage session and channels for the app protocols
    IEnumerable<IChannelRequest> GetBlockingSubDialRequestsEnumerable();

    IChannelRequest? SpecificProtocolRequest { get; set; }

    event RemotePeerConnected OnRemotePeerConnection;
    event ListenerReady OnListenerReady;

    void Connected(IPeer peer);
    void ListenerReady();
    void RequestDial(IId protocol);
    #endregion
}

public delegate void RemotePeerConnected(IRemotePeer peer);
public delegate void ListenerReady();
