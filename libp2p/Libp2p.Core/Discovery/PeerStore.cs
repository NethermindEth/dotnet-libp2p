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

    /// <remarks>
    /// Records arrive from remote peers, for example through gossipsub peer
    /// exchange, so a malformed one is ignored the same way as one that fails
    /// verification. That includes a payload its sender validly signed but that
    /// is not a peer record.
    /// </remarks>
    public void Discover(ByteString signedPeerRecord)
    {
        PublicKey publicKey;
        PeerRecord peerRecord;
        try
        {
            SignedEnvelope signedEnvelope = SignedEnvelope.Parser.ParseFrom(signedPeerRecord);
            publicKey = PublicKey.Parser.ParseFrom(signedEnvelope.PublicKey);

            if (!SigningHelper.VerifyPeerRecord(signedEnvelope, publicKey, out _))
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

        if (addresses.Length == 0)
        {
            return;
        }

        Discover(addresses);
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
            PeerInfo? newOne = null;
            PeerInfo peerInfo = _store.GetOrAdd(peerId, (id) => newOne = new PeerInfo { Addrs = [.. addrs] });

            // If peer already existed, update its addresses
            if (peerInfo != newOne)
            {
                // Only skip if addresses are identical
                if (peerInfo.Addrs is not null && addrs.UnorderedSequenceEqual(peerInfo.Addrs))
                {
                    return;
                }

                // Update addresses for existing peer
                peerInfo.Addrs = [.. addrs];
            }

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
        return _store.GetOrAdd(peerId, id => new PeerInfo());
    }

    public bool TryGetPeerInfo(PeerId peerId, out PeerInfo? peerInfo)
    {
        return _store.TryGetValue(peerId, out peerInfo);
    }

    public class PeerInfo
    {
        public ByteString? SignedPeerRecord { get; set; }
        public string[]? SupportedProtocols { get; set; }
        public HashSet<Multiaddress>? Addrs { get; set; }
        public ulong? Seq { get; set; }
    }
}

