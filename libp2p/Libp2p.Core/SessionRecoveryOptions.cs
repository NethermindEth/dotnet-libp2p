// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

/// <summary>Retry and address selection policy for application session recovery on a local peer.</summary>
public sealed class SessionRecoveryOptions
{
    internal static SessionRecoveryOptions Default { get; } = new();
    private readonly Func<int, TimeSpan> _retryDelay;

    /// <param name="maxAttempts">Maximum dial attempts without obtaining a ready session, including the first attempt.</param>
    /// <param name="retryDelay">
    /// Delay before each retry, counted from one and reset on readiness. A new watch dials immediately.
    /// Recovery after a lost session applies the first delay before dialing.
    /// When omitted, delays double from one second up to sixteen seconds.
    /// </param>
    /// <param name="selectAddresses">
    /// Supplies current addresses when dialing needs candidates. Every address must name the requested
    /// peer. Return an empty array to retry when addresses are unavailable. A selector exception or
    /// invalid result faults the watch. Observe cancellation. When omitted, the local peer dials by peer ID
    /// using its current peer store.
    /// </param>
    /// <remarks>Policy callbacks may run concurrently for different remote or local peers.</remarks>
    public SessionRecoveryOptions(int maxAttempts = 4, Func<int, TimeSpan>? retryDelay = null,
        Func<PeerId, CancellationToken, ValueTask<Multiaddress[]>>? selectAddresses = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        MaxAttempts = maxAttempts;
        _retryDelay = retryDelay ?? DefaultDelay;
        SelectAddresses = selectAddresses;
    }

    public int MaxAttempts { get; }
    internal Func<PeerId, CancellationToken, ValueTask<Multiaddress[]>>? SelectAddresses { get; }

    internal TimeSpan GetRetryDelay(int retry)
    {
        TimeSpan delay = _retryDelay(retry);
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Retry delay must be a finite, nonnegative timer interval.");
        return delay;
    }

    private static TimeSpan DefaultDelay(int retry) => TimeSpan.FromSeconds(1 << Math.Min(retry - 1, 4));
}
