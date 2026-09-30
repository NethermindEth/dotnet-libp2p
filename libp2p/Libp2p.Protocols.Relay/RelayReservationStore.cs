// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Relay;

/// <summary>
/// In-memory store for relay slot reservations. Reservations are valid until <see cref="ExpireUtcSeconds"/> or until removed.
/// </summary>
public class RelayReservationStore : IRelayReservationStore
{
    private readonly ConcurrentDictionary<PeerId, ReservationEntry> _reservations = new();

    public void Add(PeerId peer, ulong expireUtcSeconds, IReadOnlyList<byte[]> addrs, byte[]? voucher, ISessionContext sessionContext)
    {
        _reservations[peer] = new ReservationEntry(peer, expireUtcSeconds, addrs, voucher, sessionContext);
    }

    public ReservationEntry? TryGet(PeerId peer)
    {
        if (!_reservations.TryGetValue(peer, out ReservationEntry? entry))
        {
            return null;
        }

        ulong nowUtc = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (entry.ExpireUtcSeconds <= nowUtc)
        {
            _reservations.TryRemove(peer, out _);
            return null;
        }

        return entry;
    }

    public void Remove(PeerId peer)
    {
        _reservations.TryRemove(peer, out _);
    }
}
