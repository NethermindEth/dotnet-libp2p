// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>Controls when the router retries an eligible pubsub connection.</summary>
public interface IPubsubReconnectPolicy
{
    /// <summary>
    /// Returns the delay before the next attempt, or null to end this recovery. Return a delay
    /// between zero and <see cref="int.MaxValue"/> milliseconds. The router still enforces
    /// <see cref="PubsubSettings.ReconnectionAttempts"/>, address eligibility and concurrency limits.
    /// Implementations should return promptly. The router calls this outside its routing lock.
    /// </summary>
    TimeSpan? GetRetryDelay(PubsubReconnectAttempt attempt);
}

/// <summary>The peer, original cause of recovery, and one-based retry number.</summary>
public readonly record struct PubsubReconnectAttempt(PeerId PeerId, PubsubReconnectReason Reason, int AttemptNumber);

public enum PubsubReconnectReason
{
    DiscoveryDialFailed,
    StreamClosed,
    ReplyFailed,
}
