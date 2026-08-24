// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>
/// A bounded sliding-window cache for Gossipsub gossip and IWANT responses.
/// Messages are retained for a fixed number of heartbeat windows, while IHAVE
/// advertisements use only the most recent windows.
/// </summary>
internal sealed class MessageCache
{
    private sealed class Entry(MessageId id, Message message, int size)
    {
        public MessageId Id { get; } = id;
        public string Topic { get; } = message.Topic;
        public Message Message { get; } = message;
        public int Size { get; } = size;
        public LinkedListNode<Entry>? InsertionNode { get; set; }
    }

    private readonly object sync = new();
    private readonly Dictionary<MessageId, Entry> messages = [];
    private readonly LinkedList<Entry> insertionOrder = [];
    private readonly List<Entry>[] history;
    private readonly int gossipWindows;
    private readonly int maxEntries;
    private readonly long maxBytes;
    private long cachedBytes;

    public MessageCache(int gossipWindows, int historyWindows, int maxEntries, long maxBytes)
    {
        if (historyWindows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(historyWindows));
        }

        if (gossipWindows <= 0 || gossipWindows > historyWindows)
        {
            throw new ArgumentOutOfRangeException(nameof(gossipWindows));
        }

        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        this.gossipWindows = gossipWindows;
        this.maxEntries = maxEntries;
        this.maxBytes = maxBytes;
        history = Enumerable.Range(0, historyWindows).Select(_ => new List<Entry>()).ToArray();
    }

    public void Put(MessageId id, Message message)
    {
        lock (sync)
        {
            if (messages.ContainsKey(id))
            {
                return;
            }

            int size = message.CalculateSize();
            if (size > maxBytes)
            {
                return;
            }

            while (messages.Count >= maxEntries || cachedBytes + size > maxBytes)
            {
                LinkedListNode<Entry>? oldest = insertionOrder.First;
                if (oldest is null)
                {
                    return;
                }

                Remove(oldest.Value);
            }

            Entry entry = new(id, message, size);
            entry.InsertionNode = insertionOrder.AddLast(entry);
            messages.Add(id, entry);
            history[0].Add(entry);
            cachedBytes += size;
        }
    }

    public bool TryGet(MessageId id, out Message message)
    {
        lock (sync)
        {
            if (messages.TryGetValue(id, out Entry? entry))
            {
                message = entry.Message;
                return true;
            }
        }

        message = null!;
        return false;
    }

    public IReadOnlyList<MessageId> GetGossipIds(string topic)
    {
        lock (sync)
        {
            List<MessageId> result = [];
            for (int window = 0; window < gossipWindows; window++)
            {
                foreach (Entry entry in history[window])
                {
                    if (entry.Topic == topic && messages.ContainsKey(entry.Id))
                    {
                        result.Add(entry.Id);
                    }
                }
            }

            return result;
        }
    }

    public void Shift()
    {
        lock (sync)
        {
            foreach (Entry entry in history[^1])
            {
                Remove(entry);
            }

            for (int window = history.Length - 1; window > 0; window--)
            {
                history[window] = history[window - 1];
            }

            history[0] = [];
        }
    }

    internal int Count
    {
        get
        {
            lock (sync)
            {
                return messages.Count;
            }
        }
    }

    internal long CachedBytes
    {
        get
        {
            lock (sync)
            {
                return cachedBytes;
            }
        }
    }

    private void Remove(Entry entry)
    {
        if (!messages.Remove(entry.Id))
        {
            return;
        }

        cachedBytes -= entry.Size;
        insertionOrder.Remove(entry.InsertionNode!);
        entry.InsertionNode = null;
    }
}
