// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.AutoNat;

/// <summary>Sliding one-minute windows for all requests, per-peer requests and dial-data requests.</summary>
internal sealed class AutoNatRateLimiter(AutoNatV2ServerSettings settings, TimeProvider timeProvider)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Lock _lock = new();
    private readonly Queue<(PeerId Peer, DateTimeOffset Time)> _requests = new();
    private readonly Queue<DateTimeOffset> _dialDataRequests = new();
    private readonly Dictionary<PeerId, int> _peerRequests = [];
    private readonly Dictionary<PeerId, int> _inProgress = [];

    public bool TryAccept(PeerId peer)
    {
        lock (_lock)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            Expire(now);
            if (_requests.Count >= settings.RequestsPerMinute ||
                _peerRequests.GetValueOrDefault(peer) >= settings.RequestsPerPeerPerMinute ||
                _inProgress.GetValueOrDefault(peer) >= settings.MaxConcurrentRequestsPerPeer)
            {
                return false;
            }

            _requests.Enqueue((peer, now));
            _peerRequests[peer] = _peerRequests.GetValueOrDefault(peer) + 1;
            _inProgress[peer] = _inProgress.GetValueOrDefault(peer) + 1;
            return true;
        }
    }

    public bool TryAcceptDialData()
    {
        lock (_lock)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            Expire(now);
            if (_dialDataRequests.Count >= settings.DialDataRequestsPerMinute) return false;
            _dialDataRequests.Enqueue(now);
            return true;
        }
    }

    public void Complete(PeerId peer)
    {
        lock (_lock)
        {
            if (--_inProgress[peer] == 0) _inProgress.Remove(peer);
        }
    }

    private void Expire(DateTimeOffset now)
    {
        while (_requests.TryPeek(out (PeerId Peer, DateTimeOffset Time) request) && now - request.Time >= Window)
        {
            _requests.Dequeue();
            if (--_peerRequests[request.Peer] == 0) _peerRequests.Remove(request.Peer);
        }

        while (_dialDataRequests.TryPeek(out DateTimeOffset time) && now - time >= Window)
        {
            _dialDataRequests.Dequeue();
        }
    }
}
