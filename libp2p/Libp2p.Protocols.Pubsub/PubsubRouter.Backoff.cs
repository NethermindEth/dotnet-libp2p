// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public partial class PubsubRouter
{
    // Local defensive policy, not a wire protocol limit. Compare as ulong before conversion.
    private const ulong MaxRemoteBackoffSeconds = 24 * 60 * 60;

    private static TimeSpan LocalBackoff(int milliseconds) => TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));

    private TimeSpan ReceivedBackoff(ulong seconds) => seconds == 0
        ? LocalBackoff(_settings.PruneBackoff)
        : TimeSpan.FromSeconds(Math.Min(seconds, MaxRemoteBackoffSeconds));

    private static DateTime BackoffDeadline(DateTime now, TimeSpan duration) =>
        now.AddTicks(Math.Min(duration.Ticks, DateTime.MaxValue.Ticks - now.Ticks));

    // All backoff state is owned by the router lock and uses the injected UTC clock.
    private void RefreshBackoff(PeerId peerId, string topic, TimeSpan duration)
    {
        if (!peerState.TryGetValue(peerId, out PubsubPeer? peer)) return;

        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime until = BackoffDeadline(now, duration);
        if (peer.Backoff.TryGetValue(topic, out DateTime existing) && existing > until)
        {
            until = existing;
        }

        // A refresh never shortens a valid deadline, but cannot retain an unbounded one.
        TimeSpan horizon = TimeSpan.FromSeconds(MaxRemoteBackoffSeconds);
        horizon = horizon > LocalBackoff(_settings.PruneBackoff) ? horizon : LocalBackoff(_settings.PruneBackoff);
        horizon = horizon > LocalBackoff(_settings.UnsubscribeBackoff) ? horizon : LocalBackoff(_settings.UnsubscribeBackoff);
        DateTime latest = BackoffDeadline(now, horizon);
        peer.Backoff[topic] = until < latest ? until : latest;
    }

    private ControlPrune CreatePrune(PeerId peerId, string topic, int backoffMilliseconds)
    {
        ControlPrune prune = new() { TopicID = topic };
        if (peerState.TryGetValue(peerId, out PubsubPeer? peer) && peer.SupportsPeerExchange)
        {
            // Round up so the whole locally enforced interval is represented on the wire.
            prune.Backoff = (ulong)Math.Ceiling(Math.Max(0, backoffMilliseconds) / 1000.0);
        }
        return prune;
    }
}
