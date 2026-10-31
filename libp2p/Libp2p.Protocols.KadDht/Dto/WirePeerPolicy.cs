// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht;

/// <summary>Bounds conversion work separately from retained payload and protobuf framing.</summary>
internal sealed class WirePeerPolicy
{
    private readonly KadDhtOptions _options;
    private long _encoded;
    private long _retained;

    internal WirePeerPolicy(KadDhtOptions options)
    {
        Validate(options);
        _options = options;
    }

    // Exclude the requester before Append applies K, with a separate inspection budget.
    internal static IEnumerable<DhtNode> FilterFindNodeCandidates(
        IEnumerable<DhtNode> candidates, PeerId? requester, int kSize)
    {
        int remaining = kSize == int.MaxValue ? int.MaxValue : kSize + 1;
        foreach (var node in candidates)
        {
            if (requester is null || !node.PeerId.Equals(requester)) yield return node;
            if (--remaining == 0) yield break;
        }
    }

    internal static void Validate(KadDhtOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.KSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPeerIdBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxAddressesPerPeer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPeerRecordBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPeerResponseBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxProvidersPerKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxAddressesPerProvider);
    }

    // Multihash is two minimal unsigned varints followed by exactly the declared digest.
    // Do not restrict the hash-code registry: structurally valid unfamiliar codes remain usable.
    internal static bool IsValidIdentity(ReadOnlySpan<byte> bytes, int maxBytes)
    {
        if (bytes.Length > maxBytes) return false;
        int offset = 0;
        return ReadVarint(bytes, ref offset, out _) &&
            ReadVarint(bytes, ref offset, out ulong length) && length == (ulong)(bytes.Length - offset);
    }

    private static bool ReadVarint(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 63 && offset < bytes.Length; shift += 7)
        {
            byte next = bytes[offset++];
            value |= (ulong)(next & 0x7f) << shift;
            if ((next & 0x80) == 0) return shift == 0 || next != 0;
        }
        return false;
    }

    internal (DhtNode[] Closer, DhtNode[] Providers) ReadResponse(Message response, Action<DhtNode>? discovered = null, bool includeProviders = true)
    {
        var seen = new HashSet<PeerId>();
        // Providers answer the query; closer peers use the remaining shared budget.
        var providers = includeProviders ? ReadPeers(response.ProviderPeers, _options.MaxProvidersPerKey, true, seen, discovered) : Array.Empty<DhtNode>();
        var closer = ReadPeers(response.CloserPeers, _options.KSize, false, seen, discovered);
        return (closer, providers);
    }

    private DhtNode[] ReadPeers(IReadOnlyList<Message.Types.Peer> peers, int limit, bool provider,
        HashSet<PeerId> seen, Action<DhtNode>? discovered)
    {
        var result = new List<DhtNode>();
        var inList = new HashSet<ByteString>();
        for (int i = 0; i < Math.Min(peers.Count, limit); i++)
        {
            if (!IsValidIdentity(peers[i].Id.Span, _options.MaxPeerIdBytes) || !inList.Add(peers[i].Id)) continue;
            var node = ReadPeer(peers[i], provider ? _options.MaxAddressesPerProvider : _options.MaxAddressesPerPeer);
            if (node is null || (!provider && node.Multiaddrs.Count == 0)) continue;
            result.Add(node);
            if (node.Multiaddrs.Count > 0 && seen.Add(node.PeerId)) discovered?.Invoke(node);
        }
        return result.ToArray();
    }

    internal DhtNode? ReadPeer(Message.Types.Peer peer, int addressLimit)
    {
        if (!IsValidIdentity(peer.Id.Span, _options.MaxPeerIdBytes)) return null;
        // Both PeerId and the routing PublicKey own a copy of the identity.
        long retained = 2L * peer.Id.Length;
        int encoded = 1 + CodedOutputStream.ComputeBytesSize(peer.Id) +
            (peer.HasConnection ? 1 + CodedOutputStream.ComputeEnumSize((int)peer.Connection) : 0);
        if (!StartPeer(encoded, retained)) return null;
        var id = new PeerId(peer.Id.ToByteArray());
        var addresses = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Math.Min(peer.Addrs.Count, addressLimit); i++)
        {
            var bytes = peer.Addrs[i];
            int size = 1 + CodedOutputStream.ComputeBytesSize(bytes);
            // Charge parsing work even when the address is malformed or a duplicate.
            if (!AdmitEncoded(ref encoded, size)) continue;
            try
            {
                var address = Multiaddress.Decode(bytes.ToByteArray());
                var destination = address.GetPeerId();
                if (destination is not null && !destination.Equals(id)) continue;
                string normalized = destination is null ? address.Add<P2P>(id.ToString()).ToString() : address.ToString();
                if (seen.Add(normalized) && Retain(ref retained, 2L * normalized.Length)) addresses.Add(normalized);
            }
            catch
            {
                // A malformed address still consumed its bounded inspection slot and byte budget.
            }
        }
        return new DhtNode { PeerId = id, PublicKey = new PublicKey(id.Bytes), Multiaddrs = addresses.ToArray() };
    }

    internal void AppendResponse(Message response, IEnumerable<DhtNode>? closer, IEnumerable<DhtNode>? providers,
        Func<PeerId, Message.Types.ConnectionType>? connection = null)
    {
        // Preserve provider records before spending the shared budget on fallback peers.
        Append(response, providers, true, new HashSet<PeerId>(), connection);
        Append(response, closer, false, new HashSet<PeerId>(), connection);
    }

    private void Append(Message response, IEnumerable<DhtNode>? nodes, bool provider, HashSet<PeerId> seen,
        Func<PeerId, Message.Types.ConnectionType>? connection)
    {
        if (nodes is null) return;
        int limit = provider ? _options.MaxProvidersPerKey : _options.KSize;
        foreach (var node in nodes.Take(limit))
        {
            if (!IsValidIdentity(node.PeerId.Bytes, _options.MaxPeerIdBytes) || !seen.Add(node.PeerId)) continue;
            int envelopeRemaining = _options.MaxMessageSize - response.CalculateSize();
            var peer = WritePeer(node, provider, envelopeRemaining, connection?.Invoke(node.PeerId) ?? Message.Types.ConnectionType.NotConnected);
            if (peer is null) continue;
            (provider ? response.ProviderPeers : response.CloserPeers).Add(peer);
        }
    }

    private Message.Types.Peer? WritePeer(DhtNode node, bool provider, int envelopeRemaining, Message.Types.ConnectionType connection)
    {
        int encoded = 1 + CodedOutputStream.ComputeLengthSize(node.PeerId.Bytes.Length) + node.PeerId.Bytes.Length +
            1 + CodedOutputStream.ComputeEnumSize((int)connection);
        long retained = 2L * node.PeerId.Bytes.Length;
        if (FramedSize(encoded) > envelopeRemaining || !StartPeer(encoded, retained)) return null;
        var peer = new Message.Types.Peer { Id = ByteString.CopyFrom(node.PeerId.Bytes), Connection = connection };
        int limit = provider ? _options.MaxAddressesPerProvider : _options.MaxAddressesPerPeer;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in node.Multiaddrs.Take(limit))
        {
            if (raw is null) continue;
            // Do not invoke the string parser on an arbitrarily large custom-store address.
            long stringBytes = 2L * raw.Length;
            // Bound even the whitespace scan before skipping blank entries without charging bytes.
            if (!CanRetain(retained, stringBytes) || string.IsNullOrWhiteSpace(raw)) continue;
            ChargeRetained(ref retained, stringBytes);
            try
            {
                var address = Multiaddress.Decode(raw);
                var destination = address.GetPeerId();
                if (destination is not null && !destination.Equals(node.PeerId)) continue;
                // Provider storage deliberately preserves address slots, including duplicates (#407).
                if (!provider && !seen.Add(raw)) continue;
                byte[] bytes = address.ToBytes();
                int size = 1 + CodedOutputStream.ComputeLengthSize(bytes.Length) + bytes.Length;
                if (FramedSize(encoded + size) > envelopeRemaining || !AdmitEncoded(ref encoded, size)) continue;
                peer.Addrs.Add(ByteString.CopyFrom(bytes));
            }
            catch
            {
                // Invalid custom-store/callback addresses consume their inspection slot.
            }
        }
        return peer;
    }

    private static long FramedSize(int encoded) => 1L + CodedOutputStream.ComputeLengthSize(encoded) + encoded;

    private bool StartPeer(int encoded, long retained)
    {
        if (encoded > _options.MaxPeerRecordBytes || _encoded + FramedSize(encoded) > _options.MaxPeerResponseBytes ||
            !CanRetain(0, retained)) return false;
        _encoded += FramedSize(encoded);
        _retained += retained;
        return true;
    }

    private bool AdmitEncoded(ref int encoded, int size)
    {
        long next = (long)encoded + size;
        if (next > _options.MaxPeerRecordBytes) return false;
        long growth = FramedSize((int)next) - FramedSize(encoded);
        if (_encoded + growth > _options.MaxPeerResponseBytes) return false;
        _encoded += growth;
        encoded = (int)next;
        return true;
    }

    private bool CanRetain(long retained, long size) => retained + size <= _options.MaxPeerRecordBytes &&
        _retained + size <= _options.MaxPeerResponseBytes;

    private bool Retain(ref long retained, long size)
    {
        if (!CanRetain(retained, size)) return false;
        ChargeRetained(ref retained, size);
        return true;
    }

    private void ChargeRetained(ref long retained, long size)
    {
        retained += size;
        _retained += size;
    }
}
