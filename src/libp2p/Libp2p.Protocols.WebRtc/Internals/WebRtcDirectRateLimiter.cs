// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal sealed class WebRtcDirectRateLimiter
{
    private readonly Func<long> _nowMs;
    private readonly TokenBucket _globalMessageBucket;
    private readonly TokenBucket _globalByteBucket;
    private readonly ConcurrentDictionary<IPAddress, EndpointState> _endpointBuckets = new();
    private readonly int _maxPacketBytes;
    private readonly int _maxEndpointBuckets;
    private readonly long _endpointTtlMs;
    private int _cleanupFlag;

    public WebRtcDirectRateLimiter(
        int maxPacketBytes = 64 * 1024,
        int maxEndpointBuckets = 4096,
        int endpointBurst = 20,
        double endpointRatePerSecond = 10,
        int globalMessageBurst = 200,
        double globalMessageRatePerSecond = 100,
        int globalByteBurst = 2 * 1024 * 1024,
        double globalByteRatePerSecond = 512 * 1024,
        TimeSpan? endpointTtl = null,
        Func<long>? nowMs = null)
    {
        _maxPacketBytes = maxPacketBytes;
        _maxEndpointBuckets = maxEndpointBuckets;
        _endpointTtlMs = (long)(endpointTtl ?? TimeSpan.FromMinutes(5)).TotalMilliseconds;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _globalMessageBucket = new(globalMessageBurst, globalMessageRatePerSecond, _nowMs());
        _globalByteBucket = new(globalByteBurst, globalByteRatePerSecond, _nowMs());
        EndpointBurst = endpointBurst;
        EndpointRatePerSecond = endpointRatePerSecond;
    }

    public int EndpointBurst { get; }
    public double EndpointRatePerSecond { get; }

    public bool TryAccept(IPEndPoint remoteEndpoint, int packetBytes, out string? rejectReason)
    {
        long now = _nowMs();

        if (packetBytes <= 0)
        {
            rejectReason = "empty-packet";
            return false;
        }

        if (packetBytes > _maxPacketBytes)
        {
            rejectReason = "packet-too-large";
            return false;
        }

        if (!_globalMessageBucket.TryConsume(1, now))
        {
            rejectReason = "global-message-rate";
            return false;
        }

        if (!_globalByteBucket.TryConsume(packetBytes, now))
        {
            rejectReason = "global-byte-rate";
            return false;
        }

        EndpointState endpointState = _endpointBuckets.GetOrAdd(remoteEndpoint.Address, _ => new EndpointState(new TokenBucket(EndpointBurst, EndpointRatePerSecond, now), now));
        endpointState.Touch(now);

        if (!endpointState.Bucket.TryConsume(1, now))
        {
            rejectReason = "endpoint-message-rate";
            return false;
        }

        if (_endpointBuckets.Count > _maxEndpointBuckets)
        {
            Cleanup(now);
        }

        rejectReason = null;
        return true;
    }

    private void Cleanup(long now)
    {
        if (Interlocked.Exchange(ref _cleanupFlag, 1) == 1)
        {
            return;
        }

        try
        {
            foreach ((IPAddress key, EndpointState value) in _endpointBuckets)
            {
                if (now - value.LastSeenMs > _endpointTtlMs)
                {
                    _endpointBuckets.TryRemove(key, out _);
                }
            }

            if (_endpointBuckets.Count <= _maxEndpointBuckets)
            {
                return;
            }

            foreach ((IPAddress key, EndpointState _) in _endpointBuckets.OrderBy(kvp => kvp.Value.LastSeenMs).Take(_endpointBuckets.Count - _maxEndpointBuckets))
            {
                _endpointBuckets.TryRemove(key, out _);
            }
        }
        finally
        {
            Volatile.Write(ref _cleanupFlag, 0);
        }
    }

    private sealed class EndpointState
    {
        public EndpointState(TokenBucket bucket, long now)
        {
            Bucket = bucket;
            LastSeenMs = now;
        }

        public TokenBucket Bucket { get; }
        public long LastSeenMs;

        public void Touch(long now)
        {
            Volatile.Write(ref LastSeenMs, now);
        }
    }

    private sealed class TokenBucket
    {
        private readonly object _gate = new();
        private readonly double _capacity;
        private readonly double _refillPerSecond;
        private double _tokens;
        private long _lastRefillMs;

        public TokenBucket(double capacity, double refillPerSecond, long nowMs)
        {
            _capacity = capacity;
            _refillPerSecond = refillPerSecond;
            _tokens = capacity;
            _lastRefillMs = nowMs;
        }

        public bool TryConsume(double cost, long nowMs)
        {
            lock (_gate)
            {
                long elapsedMs = Math.Max(0, nowMs - _lastRefillMs);
                if (elapsedMs > 0)
                {
                    _tokens = Math.Min(_capacity, _tokens + (_refillPerSecond * elapsedMs / 1000.0));
                    _lastRefillMs = nowMs;
                }

                if (_tokens < cost)
                {
                    return false;
                }

                _tokens -= cost;
                return true;
            }
        }
    }
}
