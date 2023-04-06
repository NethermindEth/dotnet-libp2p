// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IPeerContext
{
    IPeer LocalPeer { get; }
    IPeer RemotePeer { get; }

    IEnumerable<IProtocol> ApplayerProtocols { get; }
    MultiAddr RemoteEndpoint { get; set; }
    MultiAddr LocalEndpoint { get; set; }
    IPeerContext Fork();
}
