// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core.Discovery;

/// <summary>A read-only snapshot of known metadata for a peer.</summary>
public sealed record PeerMetadata
{
    public PeerMetadata(IEnumerable<Multiaddress>? addresses = null, IEnumerable<string>? supportedProtocols = null,
        string? agentVersion = null, ByteString? signedPeerRecord = null, ulong? peerRecordSequence = null)
    {
        Addresses = addresses is null ? null : Array.AsReadOnly(addresses.ToArray());
        SupportedProtocols = supportedProtocols is null ? null : Array.AsReadOnly(supportedProtocols.ToArray());
        AgentVersion = agentVersion;
        SignedPeerRecord = signedPeerRecord;
        PeerRecordSequence = peerRecordSequence;
    }

    /// <summary>The retained address list. Its <see cref="Multiaddress"/> objects are shared and should be treated as read-only.</summary>
    public IReadOnlyList<Multiaddress>? Addresses { get; internal init; }
    public IReadOnlyList<string>? SupportedProtocols { get; internal init; }
    public string? AgentVersion { get; internal init; }
    public ByteString? SignedPeerRecord { get; internal init; }

    /// <summary>The sequence of the retained verified signed record, or null if there is none.</summary>
    public ulong? PeerRecordSequence { get; internal init; }
}
