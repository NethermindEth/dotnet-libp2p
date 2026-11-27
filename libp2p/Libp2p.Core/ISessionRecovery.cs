// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>Maintains sessions for the remote peers that applications are actively watching.</summary>
public interface ISessionRecovery
{
    /// <summary>Watches the recovery state for one remote peer, starting or joining recovery on enumeration.</summary>
    /// <remarks>
    /// Updates are snapshots of the latest state. A slow reader may skip intermediate updates.
    /// The stream yields its terminal update before completing. Canceling or disposing one enumeration
    /// releases only that interest. The last observer leaving stops the shared recovery work.
    /// A ready update can reuse an existing session. Check <see cref="ISession.Closed"/> before using it.
    /// </remarks>
    IAsyncEnumerable<SessionRecoveryUpdate> WatchAsync(PeerId remotePeerId, CancellationToken cancellationToken = default);
}

/// <summary>The progress of one recovery operation.</summary>
public enum SessionRecoveryState
{
    /// <summary>Seeking the initial session.</summary>
    Connecting,
    /// <summary>An initialized session is available.</summary>
    Ready,
    /// <summary>Waiting for or running another dial attempt.</summary>
    Retrying,
    /// <summary>Recovery ended due to session closure or peer shutdown.</summary>
    Stopped,
    /// <summary>All dial attempts for this outage failed.</summary>
    Exhausted,
    /// <summary>Recovery ended because of an invalid session or a policy error.</summary>
    Faulted
}

/// <summary>A snapshot of the state shared by observers of one recovery operation.</summary>
/// <param name="State">The current lifecycle state.</param>
/// <param name="Session">The initialized session, available in the ready state.</param>
/// <param name="Attempt">The current or failed dial attempt, numbered from one within each outage.</param>
/// <param name="CloseReason">The cause of closure when a session closed.</param>
/// <param name="Error">The most recent failure when one is available.</param>
public sealed record SessionRecoveryUpdate(
    SessionRecoveryState State,
    ISession? Session = null,
    int Attempt = 0,
    SessionCloseReason CloseReason = SessionCloseReason.Unknown,
    Exception? Error = null)
{
    public bool IsTerminal => State is SessionRecoveryState.Stopped or SessionRecoveryState.Exhausted or SessionRecoveryState.Faulted;
}
