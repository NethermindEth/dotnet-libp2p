// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Libp2p.Core.Discovery;

public class PeerStore : IPeerStore
{
    private readonly ConcurrentDictionary<PeerId, Entry> _store = [];
    private readonly LinkedList<PeerId> _discovered = [];
    private readonly LinkedList<PeerId> _configured = [];
    private readonly Lock _storeLock = new();
    private readonly int _maxPeers;

    /// <summary>Retains at most 10,000 peers, protecting active sessions and locally seeded peers from remote discovery.</summary>
    public PeerStore() : this(10_000) { }

    /// <summary>Retains at most <paramref name="maxPeers"/> peers. Remote admission fails when every slot belongs to an active session or configured peer.</summary>
    public PeerStore(int maxPeers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPeers);
        _maxPeers = maxPeers;
    }

    /// <inheritdoc />
    public void Discover(ByteString signedPeerRecord, State? state = null)
    {
        PublicKey publicKey;
        SignedEnvelope signedEnvelope;
        PeerRecord peerRecord;
        ulong seq;
        try
        {
            signedEnvelope = SignedEnvelope.Parser.ParseFrom(signedPeerRecord);
            publicKey = PublicKeyDecoder.Decode(signedEnvelope.PublicKey);

            if (!SigningHelper.VerifyPeerRecord(signedEnvelope, publicKey, keyFromEnvelope: true, out seq))
            {
                return;
            }

            peerRecord = PeerRecord.Parser.ParseFrom(signedEnvelope.Payload);
        }
        catch (Exception e) when (e is InvalidProtocolBufferException or Libp2pException)
        {
            return;
        }

        PeerId peerId = new Identity(publicKey).PeerId;
        Multiaddress[] addresses = peerRecord.Addresses
            .Select(ai => Multiaddress.Decode(ai.Multiaddr.ToByteArray()))
            .Where(a => a.GetPeerId() == peerId)
            .ToArray();
        HashSet<Multiaddress> addressSet = [.. addresses];
        bool announce = false;

        if (state is null)
        {
            lock (_storeLock)
            {
                // A valid signature authenticates the record, not its source.
                // PX entries remain in the ordinary discovered eviction tier.
                Entry? entry = GetOrAdd(peerId, isConfigured: false, required: false);
                if (entry is null) return;
                announce = StoreRecord(entry, replaceConfiguredAddresses: false);
                if (!announce) return;
            }
            _onNewPeer?.Invoke(addresses);
            return;
        }
        if (state.RemotePeerId != peerId) return;
        lock (state.PeerRecordLock)
        {
            lock (_storeLock)
            {
                Entry entry = GetOrAdd(peerId)!;
                if (entry.Metadata.PeerRecordSequence is { } cachedSequence) state.RecordVerifiedPeerRecordSequence(cachedSequence);
                if (state.RecordVerifiedPeerRecordSequence(seq) == seq)
                {
                    announce = StoreRecord(entry, replaceConfiguredAddresses: true);
                }
            }
        }
        if (announce) _onNewPeer?.Invoke(addresses);

        // Caller holds _storeLock; the envelope, sequence and addresses are published together.
        bool StoreRecord(Entry entry, bool replaceConfiguredAddresses)
        {
            PeerMetadata metadata = entry.Metadata;
            ulong? storedSequence = metadata.PeerRecordSequence;
            bool newer = storedSequence is null || seq > storedSequence;
            // Peer exchange may defer a configured peer's addresses until its own session
            // presents the same payload. A conflicting record at the same sequence stays rejected.
            if (!newer && (seq < storedSequence || !replaceConfiguredAddresses ||
                entry.DeferredSignedRecordPayload is not { } deferred || deferred != signedEnvelope.Payload)) return false;

            if (newer) entry.DeferredSignedRecordPayload = null;
            IReadOnlyList<Multiaddress>? updatedAddresses = null;
            if (addresses.Length > 0)
            {
                if (entry.IsConfigured && !replaceConfiguredAddresses)
                {
                    entry.DeferredSignedRecordPayload = signedEnvelope.Payload;
                }
                else
                {
                    entry.DeferredSignedRecordPayload = null;
                    entry.HasSignedAddresses = true;
                    if (metadata.Addresses is null || !addressSet.SetEquals(metadata.Addresses))
                        updatedAddresses = Array.AsReadOnly(addressSet.ToArray());
                }
            }

            if (newer || updatedAddresses is not null)
            {
                entry.Metadata = metadata with
                {
                    SignedPeerRecord = newer ? signedPeerRecord : metadata.SignedPeerRecord,
                    PeerRecordSequence = newer ? seq : storedSequence,
                    Addresses = updatedAddresses ?? metadata.Addresses
                };
            }
            return updatedAddresses is not null;
        }
    }

    /// <inheritdoc />
    public void Discover(Multiaddress[] addrs, bool isTrusted = true)
    {
        if (addrs is { Length: 0 })
        {
            return;
        }

        PeerId? peerId = addrs.Select(a => a.GetPeerId()).FirstOrDefault(id => id is not null);

        if (peerId is not null)
        {
            HashSet<Multiaddress> addresses = [];
            bool filtered = false;
            foreach (Multiaddress address in addrs)
            {
                if (address.GetPeerId() == peerId) addresses.Add(address);
                else filtered = true;
            }
            lock (_storeLock)
            {
                Entry? entry = GetOrAdd(peerId, isConfigured: isTrusted, required: isTrusted);
                if (entry is null || !isTrusted && (entry.IsConfigured || entry.HasSignedAddresses)) return;
                PeerMetadata metadata = entry.Metadata;
                if (metadata.Addresses is not null && addresses.SetEquals(metadata.Addresses)) return;
                entry.Metadata = metadata with { Addresses = Array.AsReadOnly(addresses.ToArray()) };
            }
            _onNewPeer?.Invoke(filtered ? [.. addresses] : addrs);
        }
    }

    private event Action<Multiaddress[]>? _onNewPeer;

    /// <inheritdoc />
    public event Action<Multiaddress[]>? OnNewPeer
    {
        add
        {
            if (value is null)
            {
                return;
            }

            _onNewPeer += value;
            foreach (Entry entry in _store.Values)
            {
                if (entry.Metadata.Addresses is { } addresses) value.Invoke(addresses.ToArray());
            }
        }
        remove
        {
            _onNewPeer -= value;
        }
    }

    public override string ToString() => $"peerStore({_store.Count}):{string.Join(",", _store.Select(x => x.Key.ToString() ?? "null"))}";

    /// <inheritdoc />
    public void SetSupportedProtocols(PeerId peerId, IReadOnlyList<string> protocols)
    {
        IReadOnlyList<string> snapshot = Array.AsReadOnly(protocols.ToArray());
        lock (_storeLock)
        {
            Entry entry = GetOrAdd(peerId)!;
            entry.Metadata = entry.Metadata with { SupportedProtocols = snapshot };
        }
    }

    /// <inheritdoc />
    public void SetAgentVersion(PeerId peerId, string? agentVersion)
    {
        lock (_storeLock)
        {
            Entry entry = GetOrAdd(peerId)!;
            entry.Metadata = entry.Metadata with { AgentVersion = agentVersion };
        }
    }

    /// <inheritdoc />
    public void Retain(PeerId peerId)
    {
        lock (_storeLock)
        {
            Entry entry = GetOrAdd(peerId)!;
            if (entry.Sessions++ == 0) RemoveFromEvictionList(entry);
        }
    }

    /// <inheritdoc />
    public void Release(PeerId peerId)
    {
        lock (_storeLock)
        {
            Entry entry = _store[peerId];
            if (--entry.Sessions == 0) entry.EvictionNode = (entry.IsConfigured ? _configured : _discovered).AddLast(peerId);
        }
    }

    internal int GetRetainedSessionCount(PeerId peerId)
    {
        lock (_storeLock) return _store.TryGetValue(peerId, out Entry? entry) ? entry.Sessions : 0;
    }

    private Entry? GetOrAdd(PeerId peerId, bool isConfigured = false, bool required = true)
    {
        if (_store.TryGetValue(peerId, out Entry? existing))
        {
            if (isConfigured && !existing.IsConfigured)
            {
                existing.IsConfigured = true;
                RemoveFromEvictionList(existing);
                if (existing.Sessions == 0) existing.EvictionNode = _configured.AddLast(peerId);
            }
            return existing;
        }
        while (_store.Count >= _maxPeers)
        {
            LinkedListNode<PeerId>? oldest = _discovered.First ?? (isConfigured ? _configured.First : null);
            if (oldest is null)
            {
                if (required) throw new Libp2pException($"Peer store capacity {_maxPeers} is occupied by active sessions or configured peers.");
                return null;
            }
            oldest.List!.Remove(oldest);
            _store.TryRemove(oldest.Value, out _);
        }
        Entry added = new() { IsConfigured = isConfigured };
        added.EvictionNode = (isConfigured ? _configured : _discovered).AddLast(peerId);
        _store[peerId] = added;
        return added;
    }

    private static void RemoveFromEvictionList(Entry entry)
    {
        if (entry.EvictionNode is { } node)
        {
            node.List!.Remove(node);
            entry.EvictionNode = null;
        }
    }

    /// <inheritdoc />
    public bool TryGetPeerInfo(PeerId peerId, [NotNullWhen(true)] out PeerMetadata? metadata)
    {
        metadata = _store.TryGetValue(peerId, out Entry? entry) ? entry.Metadata : null;
        return metadata is not null;
    }

    private sealed class Entry
    {
        private static readonly PeerMetadata EmptyMetadata = new();
        private PeerMetadata _metadata = EmptyMetadata;

        public PeerMetadata Metadata
        {
            get => Volatile.Read(ref _metadata);
            set => Volatile.Write(ref _metadata, value);
        }

        public bool IsConfigured;
        // A newer empty record does not remove the existing addresses' protection.
        public bool HasSignedAddresses;
        // Only configured entries can defer applying a retained record's addresses.
        public ByteString? DeferredSignedRecordPayload;
        public int Sessions;
        public LinkedListNode<PeerId>? EvictionNode;
    }
}
