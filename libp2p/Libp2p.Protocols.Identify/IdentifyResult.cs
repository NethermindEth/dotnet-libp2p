// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols;

/// <summary>Metadata received from the authenticated peer after Identify verification succeeds.</summary>
/// <remarks>
/// Version strings, protocols and addresses are claims made by the peer. Only SignedPeerRecord
/// and PeerRecordSequence represent a verified signed record, which may be older than the cache.
/// Addresses retain their wire encoding and are not validated for reachability.
/// </remarks>
public sealed record IdentifyResult
{
    public required PeerId PeerId { get; init; }
    public string? AgentVersion { get; init; }
    public string? ProtocolVersion { get; init; }
    public IReadOnlyList<string> Protocols { get; init; } = [];
    public IReadOnlyList<ByteString> ListenAddresses { get; init; } = [];
    public ByteString? ObservedAddress { get; init; }
    public ByteString? SignedPeerRecord { get; init; }
    public ulong? PeerRecordSequence { get; init; }
}
