// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core;

public interface IPeerContext
{
    string Id { get; }
    IPeer LocalPeer { get; }
    IPeer RemotePeer { get; }

    MultiAddr RemoteEndpoint { get; set; }
    MultiAddr LocalEndpoint { get; set; }

    // TODO: Get rid of this:
    IPeerContext Fork();

    #region Allows muxer to manage session and channels for the app protocols
    BlockingCollection<IChannelRequest> SubDialRequests { get; }

    IChannelRequest? SpecificProtocolRequest { get; set; }

    event RemotePeerConnected OnRemotePeerConnection;

    void Connected(IPeer peer);
    #endregion
}

public delegate void RemotePeerConnected(IRemotePeer peer);
