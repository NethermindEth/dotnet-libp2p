// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>Tracks a bounded random sample of admitted IHAVE/IWANT promises.</summary>
internal sealed class IwantPromiseTracker
{
    internal sealed class Promise(MessageId id, PeerId peer, object owner)
    {
        public MessageId Id { get; } = id;
        public PeerId Peer { get; } = peer;
        public object Owner { get; } = owner;
        public DateTime? Deadline { get; set; }
    }

    private readonly object sync = new();
    private readonly Dictionary<MessageId, Dictionary<PeerId, Promise>> promises = [];
    private readonly int maxPromises;
    private int promiseCount;

    public IwantPromiseTracker(int maxPromises)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPromises);
        this.maxPromises = maxPromises;
    }

    public Promise? Reserve(PeerId peerId, object owner, IEnumerable<MessageId> messageIds)
    {
        // Reservoir sampling retains one ID, independently of the request's length.
        MessageId selected = default;
        int count = 0;
        foreach (MessageId id in messageIds)
        {
            if (Random.Shared.Next(++count) == 0) selected = id;
        }
        if (count == 0) return null;
        lock (sync)
        {
            if (!promises.TryGetValue(selected, out Dictionary<PeerId, Promise>? byPeer))
            {
                if (promiseCount >= maxPromises) return null;
                byPeer = [];
                promises.Add(selected, byPeer);
            }
            bool replacing = byPeer.ContainsKey(peerId);
            if (!replacing && promiseCount >= maxPromises) return null;
            Promise promise = new(selected, peerId, owner);
            byPeer[peerId] = promise;
            if (!replacing) promiseCount++;
            return promise;
        }
    }

    public void Arm(Promise? promise, DateTime deadline)
    {
        if (promise is null) return;
        lock (sync)
        {
            // Fulfillment or teardown may have removed it before the write completed.
            if (promises.TryGetValue(promise.Id, out var byPeer) && ReferenceEquals(byPeer.GetValueOrDefault(promise.Peer), promise))
                promise.Deadline = deadline;
        }
    }

    public void Cancel(Promise? promise)
    {
        if (promise is null) return;
        lock (sync)
        {
            if (promises.TryGetValue(promise.Id, out var byPeer) && ReferenceEquals(byPeer.GetValueOrDefault(promise.Peer), promise))
            {
                byPeer.Remove(promise.Peer);
                promiseCount--;
                if (byPeer.Count == 0) promises.Remove(promise.Id);
            }
        }
    }

    public void Fulfill(MessageId id)
    {
        lock (sync)
        {
            if (promises.Remove(id, out var byPeer)) promiseCount -= byPeer.Count;
        }
    }

    public IReadOnlyDictionary<PeerId, int> TakeExpired(DateTime now)
    {
        lock (sync)
        {
            Dictionary<PeerId, int> broken = [];
            foreach ((MessageId id, var byPeer) in promises.ToArray())
            {
                foreach ((PeerId peerId, Promise promise) in byPeer.ToArray())
                {
                    if (promise.Deadline is not DateTime deadline || deadline > now) continue;
                    byPeer.Remove(peerId);
                    promiseCount--;
                    broken[peerId] = broken.GetValueOrDefault(peerId) + 1;
                }
                if (byPeer.Count == 0) promises.Remove(id);
            }
            return broken;
        }
    }

    public void Clear(PeerId peerId) => RemoveWhere(promise => promise.Peer == peerId);
    public void ClearOwner(object owner) => RemoveWhere(promise => ReferenceEquals(promise.Owner, owner));

    private void RemoveWhere(Func<Promise, bool> predicate)
    {
        lock (sync)
        {
            foreach ((MessageId id, var byPeer) in promises.ToArray())
            {
                foreach ((PeerId peerId, Promise promise) in byPeer.ToArray())
                {
                    if (predicate(promise))
                    {
                        byPeer.Remove(peerId);
                        promiseCount--;
                    }
                }
                if (byPeer.Count == 0) promises.Remove(id);
            }
        }
    }

    internal int Count { get { lock (sync) return promiseCount; } }
}
