// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core.Discovery;

public class PeerStore
{
    private readonly ConcurrentDictionary<PeerId, PeerInfo> _store = [];
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

    /// <remarks>
    /// Records arrive from remote peers, for example through gossipsub peer
    /// exchange, so a malformed one is ignored the same way as one that fails
    /// verification. That includes a payload its sender validly signed but that
    /// is not a peer record.
    /// </remarks>
    public void Discover(ByteString signedPeerRecord) => Discover(signedPeerRecord, null);

    /// <summary>Stores newer signed records from every source, preserving the session watermark when a retained record is missing.</summary>
    public void Discover(ByteString signedPeerRecord, State? state)
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
                PeerInfo? info = GetOrAdd(peerId, isConfigured: false, required: false);
                if (info is null) return;
                announce = StoreRecord(info, replaceConfiguredAddresses: false);
                if (!announce) return;
            }
            onNewPeer?.Invoke(addresses);
            return;
        }
        if (state.RemotePeerId != peerId) return;
        lock (state.PeerRecordLock)
        {
            lock (_storeLock)
            {
                PeerInfo info = GetOrAdd(peerId)!;
                if (info.SignedPeerRecord is not null) state.RecordVerifiedPeerRecordSequence(info.Seq ?? 0);
                ulong? priorSeq = state.VerifiedPeerRecordSequence;
                state.RecordVerifiedPeerRecordSequence(seq);
                if (priorSeq is null || seq >= priorSeq)
                {
                    announce = StoreRecord(info, replaceConfiguredAddresses: true);
                }
            }
        }
        if (announce) onNewPeer?.Invoke(addresses);

        // Caller holds _storeLock; envelope and sequence are published together.
        bool StoreRecord(PeerInfo info, bool replaceConfiguredAddresses)
        {
            if (info.SignedPeerRecord is not null && seq <= (info.Seq ?? 0))
            {
                // PX can retain a configured peer's record without applying its
                // addresses. Its session may apply the same signed payload once,
                // without replacing the envelope or accepting conflicting equal sequences.
                if (seq < (info.Seq ?? 0) || !replaceConfiguredAddresses ||
                    info.DeferredSignedRecordPayload is not { } deferred || deferred != signedEnvelope.Payload) return false;
            }
            else
            {
                info.SignedPeerRecord = signedPeerRecord;
                info.Seq = seq;
                info.DeferredSignedRecordPayload = null;
            }
            if (addresses.Length == 0) return false;
            if (info.IsConfigured && !replaceConfiguredAddresses)
            {
                info.DeferredSignedRecordPayload = signedEnvelope.Payload;
                return false;
            }
            info.DeferredSignedRecordPayload = null;
            info.HasSignedAddresses = true;
            if (info.Addrs is not null && info.Addrs.SetEquals(addressSet)) return false;
            info.Addrs = addressSet;
            return true;
        }
    }

    /// <summary>Adds deliberately configured peer addresses, protected from untrusted discovery.</summary>
    public void Discover(Multiaddress[] addrs) => Discover(addrs, isTrusted: true);

    /// <summary>Uses false for peer-supplied addresses. Remote discoveries cannot evict or overwrite trusted entries.</summary>
    public void Discover(Multiaddress[] addrs, bool isTrusted)
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
                PeerInfo? peerInfo = GetOrAdd(peerId, isConfigured: isTrusted, required: isTrusted);
                if (peerInfo is null || !isTrusted && (peerInfo.IsConfigured || peerInfo.HasSignedAddresses)) return;
                if (peerInfo.Addrs is not null && peerInfo.Addrs.SetEquals(addresses)) return;
                peerInfo.Addrs = addresses;
            }
            onNewPeer?.Invoke(filtered ? [.. addresses] : addrs);
        }
    }

    private Action<Multiaddress[]>? onNewPeer = null;

    public event Action<Multiaddress[]>? OnNewPeer
    {
        add
        {
            if (value is null)
            {
                return;
            }

            onNewPeer += value;
            foreach (PeerInfo? item in _store.Select(x => x.Value).ToArray())
            {
                if (item.Addrs is not null) value.Invoke(item.Addrs.ToArray());
            }
        }
        remove
        {
            onNewPeer -= value;
        }
    }

    public override string ToString() => $"peerStore({_store.Count}):{string.Join(",", _store.Select(x => x.Key.ToString() ?? "null"))}";

    public PeerInfo GetPeerInfo(PeerId peerId)
    {
        if (_store.TryGetValue(peerId, out PeerInfo? existing)) return existing;
        lock (_storeLock)
        {
            return GetOrAdd(peerId)!;
        }
    }

    /// <summary>Publishes metadata to the current retained entry, rather than a possibly evicted reference.</summary>
    public void SetSupportedProtocols(PeerId peerId, string[] protocols)
    {
        lock (_storeLock) GetOrAdd(peerId)!.SupportedProtocols = protocols;
    }

    /// <summary>Publishes the advertised agent version to the current retained entry.</summary>
    public void SetAgentVersion(PeerId peerId, string? agentVersion)
    {
        lock (_storeLock) GetOrAdd(peerId)!.AgentVersion = agentVersion;
    }

    public ulong? GetVerifiedPeerRecordSequence(PeerId peerId)
    {
        lock (_storeLock)
        {
            return _store.TryGetValue(peerId, out PeerInfo? info) && info.SignedPeerRecord is not null ? info.Seq ?? 0 : null;
        }
    }

    internal void Retain(PeerId peerId)
    {
        lock (_storeLock)
        {
            PeerInfo info = GetOrAdd(peerId)!;
            if (info.Sessions++ == 0) RemoveFromEvictionList(info);
        }
    }

    internal void Release(PeerId peerId)
    {
        lock (_storeLock)
        {
            PeerInfo info = _store[peerId];
            if (--info.Sessions == 0) info.EvictionNode = (info.IsConfigured ? _configured : _discovered).AddLast(peerId);
        }
    }

    private PeerInfo? GetOrAdd(PeerId peerId, bool isConfigured = false, bool required = true)
    {
        if (_store.TryGetValue(peerId, out PeerInfo? existing))
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
        PeerInfo added = new() { IsConfigured = isConfigured };
        added.EvictionNode = (isConfigured ? _configured : _discovered).AddLast(peerId);
        _store[peerId] = added;
        return added;
    }

    private static void RemoveFromEvictionList(PeerInfo info)
    {
        if (info.EvictionNode is { } node)
        {
            node.List!.Remove(node);
            info.EvictionNode = null;
        }
    }

    public bool TryGetPeerInfo(PeerId peerId, out PeerInfo? peerInfo)
    {
        return _store.TryGetValue(peerId, out peerInfo);
    }

    public class PeerInfo
    {
        internal bool IsConfigured;
        // A newer empty record does not remove the existing addresses' protection.
        internal bool HasSignedAddresses;
        // Only configured entries can defer applying a retained record's addresses.
        internal ByteString? DeferredSignedRecordPayload;
        internal int Sessions;
        internal LinkedListNode<PeerId>? EvictionNode;
        public string? AgentVersion { get; set; }
        public ByteString? SignedPeerRecord { get; set; }
        public string[]? SupportedProtocols { get; set; }
        public HashSet<Multiaddress>? Addrs { get; set; }
        public ulong? Seq { get; set; }
    }
}

