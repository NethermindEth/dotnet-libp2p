// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core;

public interface IPeerContext
{
    string Id { get; }
    IPeer LocalPeer { get; }
    IPeer RemotePeer { get; }

    Multiaddr RemoteEndpoint { get; set; }
    Multiaddr LocalEndpoint { get; set; }

    // TODO: Get rid of this:
    IPeerContext Fork();

    #region Allows muxer to manage session and channels for the app protocols

    /// <summary>
    /// Request for dial with application layer protocols
    /// </summary>
    BlockingCollection<IChannelNegotiationRequest> DialRequests { get; }

    IChannelNegotiationRequest? SpecificProtocolRequest { get; set; }

    event RemotePeerConnected OnRemotePeerConnection;

    void Connected(IPeer peer);
    #endregion
}


public delegate void RemotePeerConnected(IRemotePeer peer);
