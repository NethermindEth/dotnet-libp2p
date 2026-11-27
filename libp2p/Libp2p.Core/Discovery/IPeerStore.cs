// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Libp2p.Core.Discovery;

/// <summary>Thread-safe discovery and metadata storage shared by peers and their protocols.</summary>
public interface IPeerStore
{
    /// <summary>Reports newly stored address sets and changes to known address sets.</summary>
    /// <remarks>
    /// Attaching a handler synchronously replays the retained peers that have addresses.
    /// Updates may race with replay, so consumers must tolerate duplicates and concurrent calls.
    /// Handlers execute outside storage locks and should treat the shared address objects as read-only.
    /// </remarks>
    event Action<Multiaddress[]>? OnNewPeer;

    /// <summary>Reads a snapshot of retained metadata. A missing peer returns false and null without creating an entry.</summary>
    bool TryGetPeerInfo(PeerId peerId, [NotNullWhen(true)] out PeerMetadata? metadata);

    /// <summary>Adds the addresses for the peer identified by the first peer ID in the input.</summary>
    /// <param name="addrs">
    /// Addresses to consider. Each retained address must name the same peer. Treat the address objects as read-only after publishing.
    /// </param>
    /// <param name="isTrusted">
    /// True for locally configured addresses. Remote discovery uses false and cannot displace configured peers
    /// or overwrite configured or signed addresses.
    /// </param>
    void Discover(Multiaddress[] addrs, bool isTrusted = true);

    /// <summary>Verifies a signed peer record and retains it if newer.</summary>
    /// <remarks>Ignores unverifiable envelopes and stale sequences. Decoding addresses from a verified record can fail.</remarks>
    /// <param name="signedPeerRecord">The original signed envelope, which is kept for peer exchange.</param>
    /// <param name="state">
    /// The authenticated session when the record comes from its remote peer. A session record must match the remote
    /// peer ID and may update configured addresses. Use <see cref="State.RecordVerifiedPeerRecordSequence"/>
    /// to advance the session watermark and determine whether an incoming sequence is older than it.
    /// With no session, a record from peer exchange has ordinary discovery priority.
    /// </param>
    void Discover(ByteString signedPeerRecord, State? state = null);

    /// <summary>Sets the agent version on the current entry, creating it if needed without granting configured trust.</summary>
    void SetAgentVersion(PeerId peerId, string? agentVersion);

    /// <summary>Sets the supported protocols on the current entry, creating it if needed without granting configured trust.</summary>
    void SetSupportedProtocols(PeerId peerId, IReadOnlyList<string> protocols);

    /// <summary>Creates or retains the entry for an active session, protecting it from eviction.</summary>
    /// <remarks>An implementation may reject admission if it cannot retain the peer. A failed call acquires no retention.</remarks>
    void Retain(PeerId peerId);

    /// <summary>Releases the retention acquired by one successful call to <see cref="Retain"/>.</summary>
    void Release(PeerId peerId);
}
