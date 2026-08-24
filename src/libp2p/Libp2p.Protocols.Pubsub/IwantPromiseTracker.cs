// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>
/// Tracks a bounded random sample of message promises created by IHAVE/IWANT.
/// </summary>
internal sealed class IwantPromiseTracker
{
    private readonly object sync = new();
    private readonly Dictionary<MessageId, Dictionary<PeerId, DateTime>> promises = [];
    private readonly int maxPromises;
    private int promiseCount;

    public IwantPromiseTracker(int maxPromises)
    {
        if (maxPromises <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPromises));
        }

        this.maxPromises = maxPromises;
    }

    public void Add(PeerId peerId, IReadOnlyList<MessageId> messageIds, DateTime deadline)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        MessageId id = messageIds[Random.Shared.Next(messageIds.Count)];
        lock (sync)
        {
            if (!promises.TryGetValue(id, out Dictionary<PeerId, DateTime>? byPeer))
            {
                if (promiseCount >= maxPromises)
                {
                    return;
                }

                byPeer = [];
                promises.Add(id, byPeer);
            }

            if (!byPeer.ContainsKey(peerId) && promiseCount >= maxPromises)
            {
                return;
            }

            if (byPeer.TryAdd(peerId, deadline))
            {
                promiseCount++;
            }
        }
    }

    public void Fulfill(MessageId id)
    {
        lock (sync)
        {
            if (promises.Remove(id, out Dictionary<PeerId, DateTime>? byPeer))
            {
                promiseCount -= byPeer.Count;
            }
        }
    }

    public IReadOnlyDictionary<PeerId, int> TakeExpired(DateTime now)
    {
        lock (sync)
        {
            Dictionary<PeerId, int> broken = [];
            foreach ((MessageId id, Dictionary<PeerId, DateTime> byPeer) in promises.ToArray())
            {
                foreach ((PeerId peerId, DateTime deadline) in byPeer.ToArray())
                {
                    if (deadline > now)
                    {
                        continue;
                    }

                    byPeer.Remove(peerId);
                    promiseCount--;
                    broken[peerId] = broken.GetValueOrDefault(peerId) + 1;
                }

                if (byPeer.Count == 0)
                {
                    promises.Remove(id);
                }
            }

            return broken;
        }
    }

    public void Clear(PeerId peerId)
    {
        lock (sync)
        {
            foreach ((MessageId id, Dictionary<PeerId, DateTime> byPeer) in promises.ToArray())
            {
                if (!byPeer.Remove(peerId))
                {
                    continue;
                }

                promiseCount--;
                if (byPeer.Count == 0)
                {
                    promises.Remove(id);
                }
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (sync)
            {
                return promiseCount;
            }
        }
    }
}
