// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Relay;

/// <summary>
/// Stores relay slot reservations: which peer has a reservation and the session context
/// the relay uses to open stop streams to that peer.
/// </summary>
public interface IRelayReservationStore
{
    /// <summary>
    /// Adds or updates a reservation for the given peer. Called by the relay when it accepts a RESERVE.
    /// </summary>
    void Add(PeerId peer, ulong expireUtcSeconds, IReadOnlyList<byte[]> addrs, byte[]? voucher, ISessionContext sessionContext);

    /// <summary>
    /// Tries to get the reservation and session for the given peer. Returns null if not found or expired.
    /// </summary>
    ReservationEntry? TryGet(PeerId peer);

    /// <summary>
    /// Removes the reservation for the given peer (e.g. when the connection closes).
    /// </summary>
    void Remove(PeerId peer);
}

/// <summary>
/// A single reservation entry held by the store.
/// </summary>
public record ReservationEntry(
    PeerId PeerId,
    ulong ExpireUtcSeconds,
    IReadOnlyList<byte[]> Addrs,
    byte[]? Voucher,
    ISessionContext SessionContext);
