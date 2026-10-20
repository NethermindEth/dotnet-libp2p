// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Extensions;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core.Discovery;

public class PeerStore
{
    private readonly ConcurrentDictionary<PeerId, PeerInfo> _store = [];
    private readonly Queue<PeerId> _insertionOrder = [];
    private readonly Lock _insertionLock = new();
    private readonly int _maxPeers;

    /// <summary>Retains at most 10,000 peers, evicting the oldest inserted peer when full.</summary>
    public PeerStore() : this(10_000) { }

    /// <summary>Retains at most <paramref name="maxPeers"/> peers in insertion order.</summary>
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

    /// <summary>Stores a session's verified record only when it advances the known sequence.</summary>
    public void Discover(ByteString signedPeerRecord, State? state)
    {
        PublicKey publicKey;
        PeerRecord peerRecord;
        ulong seq;
        try
        {
            SignedEnvelope signedEnvelope = SignedEnvelope.Parser.ParseFrom(signedPeerRecord);
            publicKey = PublicKey.Parser.ParseFrom(signedEnvelope.PublicKey);

            if (!SigningHelper.VerifyPeerRecord(signedEnvelope, publicKey, out seq))
            {
                return;
            }

            peerRecord = PeerRecord.Parser.ParseFrom(signedEnvelope.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }

        PeerId peerId = new Identity(publicKey).PeerId;
        Multiaddress[] addresses = peerRecord.Addresses
            .Select(ai => Multiaddress.Decode(ai.Multiaddr.ToByteArray()))
            .Where(a => a.GetPeerId() == peerId)
            .ToArray();

        if (state is null)
        {
            if (addresses.Length > 0) Discover(addresses);
            return;
        }
        if (state.RemotePeerId != peerId) return;
        bool announce = false;
        lock (state.PeerRecordLock)
        {
            PeerInfo info = GetPeerInfo(peerId);
            lock (info.RecordLock)
            {
                if (info.SignedPeerRecord is not null) state.RecordVerifiedPeerRecordSequence(info.Seq ?? 0);
                ulong? priorSeq = state.VerifiedPeerRecordSequence;
                state.RecordVerifiedPeerRecordSequence(seq);
                if (priorSeq is null || seq > priorSeq)
                {
                    info.SignedPeerRecord = signedPeerRecord;
                    info.Seq = seq;
                    if (addresses.Length > 0 && (info.Addrs is null || !addresses.UnorderedSequenceEqual(info.Addrs)))
                    {
                        info.Addrs = [.. addresses];
                        announce = true;
                    }
                }
            }
        }
        if (announce) onNewPeer?.Invoke(addresses);
    }

    public void Discover(Multiaddress[] addrs)
    {
        if (addrs is { Length: 0 })
        {
            return;
        }

        PeerId? peerId = addrs.FirstOrDefault()?.GetPeerId();

        if (peerId is not null)
        {
            PeerInfo peerInfo = GetPeerInfo(peerId);
            // Only skip if addresses are identical
            if (peerInfo.Addrs is not null && addrs.UnorderedSequenceEqual(peerInfo.Addrs))
            {
                return;
            }
            peerInfo.Addrs = [.. addrs];
            onNewPeer?.Invoke(addrs);
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
        lock (_insertionLock)
        {
            if (_store.TryGetValue(peerId, out existing)) return existing;
            if (_store.Count == _maxPeers)
            {
                _store.TryRemove(_insertionOrder.Dequeue(), out _);
            }
            PeerInfo added = new();
            _store[peerId] = added;
            _insertionOrder.Enqueue(peerId);
            return added;
        }
    }

    public bool TryGetPeerInfo(PeerId peerId, out PeerInfo? peerInfo)
    {
        return _store.TryGetValue(peerId, out peerInfo);
    }

    public class PeerInfo
    {
        internal Lock RecordLock { get; } = new();
        public ByteString? SignedPeerRecord { get; set; }
        public string[]? SupportedProtocols { get; set; }
        public HashSet<Multiaddress>? Addrs { get; set; }
        public ulong? Seq { get; set; }
    }
}

