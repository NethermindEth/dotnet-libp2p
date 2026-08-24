// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>
/// Bounded per-peer state for Gossipsub control messages.
/// </summary>
internal sealed class PeerControlState
{
    private readonly Dictionary<MessageId, long> unwanted = [];
    private readonly Dictionary<MessageId, ResponseState> iwantResponses = [];

    private readonly record struct ResponseState(int Count, long ExpiresAtTick);

    public int IHaveMessages { get; private set; }
    public int IHaveRequested { get; private set; }
    public int IwantMessages { get; private set; }
    public int IdontwantMessages { get; private set; }

    public bool TryAcceptIHave(int maxMessages)
    {
        if (IHaveMessages >= maxMessages)
        {
            return false;
        }

        IHaveMessages++;
        return true;
    }

    public int ReserveIHaveRequests(int requested, int maxRequests)
    {
        int reserved = Math.Min(requested, maxRequests - IHaveRequested);
        IHaveRequested += reserved;
        return reserved;
    }

    public bool TryAcceptIdontwant(int maxMessages)
    {
        if (IdontwantMessages >= maxMessages)
        {
            return false;
        }

        IdontwantMessages++;
        return true;
    }

    public bool TryAcceptIwant(int maxMessages)
    {
        if (IwantMessages >= maxMessages)
        {
            return false;
        }

        IwantMessages++;
        return true;
    }

    public void AddUnwanted(MessageId id, long expiresAtTick, int maxEntries)
    {
        if (unwanted.ContainsKey(id) || unwanted.Count < maxEntries)
        {
            unwanted[id] = expiresAtTick;
        }
    }

    public bool IsUnwanted(MessageId id) => unwanted.ContainsKey(id);

    public bool TryRecordIwantResponse(MessageId id, long currentTick, int historyLength, int maxResponses, int maxTrackedMessages)
    {
        if (iwantResponses.TryGetValue(id, out ResponseState response))
        {
            if (response.Count >= maxResponses)
            {
                return false;
            }

            iwantResponses[id] = response with { Count = response.Count + 1 };
            return true;
        }

        if (iwantResponses.Count >= maxTrackedMessages)
        {
            return false;
        }

        iwantResponses[id] = new ResponseState(1, currentTick + historyLength);
        return true;
    }

    public void ResetHeartbeatCounters(long currentTick)
    {
        IHaveMessages = 0;
        IHaveRequested = 0;
        IwantMessages = 0;
        IdontwantMessages = 0;
        RemoveExpired(unwanted, currentTick);
        RemoveExpired(iwantResponses, currentTick);
    }

    private static void RemoveExpired(Dictionary<MessageId, long> entries, long currentTick)
    {
        List<MessageId>? expired = null;
        foreach ((MessageId id, long expiresAtTick) in entries)
        {
            if (expiresAtTick <= currentTick)
            {
                (expired ??= []).Add(id);
            }
        }

        if (expired is not null)
        {
            foreach (MessageId id in expired)
            {
                entries.Remove(id);
            }
        }
    }

    private static void RemoveExpired(Dictionary<MessageId, ResponseState> entries, long currentTick)
    {
        List<MessageId>? expired = null;
        foreach ((MessageId id, ResponseState response) in entries)
        {
            if (response.ExpiresAtTick <= currentTick)
            {
                (expired ??= []).Add(id);
            }
        }

        if (expired is not null)
        {
            foreach (MessageId id in expired)
            {
                entries.Remove(id);
            }
        }
    }
}
