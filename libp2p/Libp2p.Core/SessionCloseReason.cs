// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>The cause of session closure as observed by the local peer.</summary>
public enum SessionCloseReason
{
    /// <summary>The session does not report a cause.</summary>
    Unknown,
    /// <summary>The remote end closed or the connection stopped working.</summary>
    ConnectionLost,
    /// <summary>The application or a local protocol requested disconnection, or an <see cref="ILocalPeer.OnConnected"/> handler failed.</summary>
    LocalDisconnect,
    /// <summary>The local peer is shutting down.</summary>
    PeerShutdown,
    /// <summary>The peer released an idle connection under inbound admission pressure.</summary>
    IdleEviction
}
