// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
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

    public void Discover(ByteString signedPeerRecord)
    {
        SignedEnvelope signedEnvelope = SignedEnvelope.Parser.ParseFrom(signedPeerRecord);
        PublicKey publicKey = PublicKey.Parser.ParseFrom(signedEnvelope.PublicKey);
        PeerId peerId = new Identity(publicKey).PeerId;

        if (!SigningHelper.VerifyPeerRecord(signedEnvelope, publicKey, out _))
        {
            return;
        }

        Multiaddress[] addresses = PeerRecord.Parser.ParseFrom(signedEnvelope.Payload).Addresses
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
            if (peerInfo != newOne && peerInfo.Addrs is not null && addrs.UnorderedSequenceEqual(peerInfo.Addrs))
            {
                return;
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

    public class PeerInfo
    {
        public ByteString? SignedPeerRecord { get; set; }
        public string[]? SupportedProtocols { get; set; }
        public HashSet<Multiaddress>? Addrs { get; set; }
        public ulong Seq { get; set; } = 0;
    }
}

