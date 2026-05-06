// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2P.Protocols.KadDht.Dto;

/// <summary>
/// Conversion helpers between internal DHT types and spec-compliant wire-format types.
/// </summary>
public static class MessageHelper
{
    /// <summary>
    /// Convert a DhtNode to a spec-compliant Message.Types.Peer for wire transmission.
    /// </summary>
    public static Message.Types.Peer ToWirePeer(DhtNode node, Message.Types.ConnectionType connection = Message.Types.ConnectionType.Connected)
    {
        var peer = new Message.Types.Peer
        {
            Id = ByteString.CopyFrom(node.PeerId.Bytes.ToArray()),
            Connection = connection
        };

        foreach (var addrStr in node.Multiaddrs)
        {
            if (string.IsNullOrWhiteSpace(addrStr)) continue;
            try
            {
                peer.Addrs.Add(ByteString.CopyFrom(Multiaddress.Decode(addrStr).ToBytes()));
            }
            catch
            {
                // Skip malformed addresses
            }
        }

        return peer;
    }

    /// <summary>
    /// Convert a spec-compliant Message.Types.Peer from wire format to internal DhtNode.
    /// </summary>
    public static DhtNode? FromWirePeer(Message.Types.Peer peer)
    {
        if (!peer.HasId || peer.Id.IsEmpty) return null;

        var idBytes = peer.Id.ToByteArray();
        var peerId = new PeerId(idBytes);
        var publicKey = new PublicKey(idBytes);

        var addrs = new List<string>();
        foreach (var addrBytes in peer.Addrs)
        {
            try
            {
                addrs.Add(Multiaddress.Decode(addrBytes.ToByteArray()).ToString());
            }
            catch
            {
                // Skip malformed addresses
            }
        }

        return new DhtNode
        {
            PeerId = peerId,
            PublicKey = publicKey,
            Multiaddrs = addrs.ToArray()
        };
    }

    /// <summary>
    /// Build a PING request message.
    /// </summary>
    public static Message CreatePingRequest() => new()
    {
        Type = Message.Types.MessageType.Ping
    };

    /// <summary>
    /// Build a PING response message.
    /// </summary>
    public static Message CreatePingResponse() => new()
    {
        Type = Message.Types.MessageType.Ping
    };

    /// <summary>
    /// Build a FIND_NODE request message.
    /// </summary>
    public static Message CreateFindNodeRequest(byte[] targetPeerId) => new()
    {
        Type = Message.Types.MessageType.FindNode,
        Key = ByteString.CopyFrom(targetPeerId)
    };

    /// <summary>
    /// Build a FIND_NODE response with closer peers.
    /// </summary>
    public static Message CreateFindNodeResponse(IEnumerable<DhtNode> closerPeers)
    {
        var msg = new Message { Type = Message.Types.MessageType.FindNode };
        foreach (var node in closerPeers)
        {
            msg.CloserPeers.Add(ToWirePeer(node));
        }
        return msg;
    }

    /// <summary>
    /// Build a PUT_VALUE request message.
    /// </summary>
    public static Message CreatePutValueRequest(byte[] key, byte[] value, string? timeReceived = null) => new()
    {
        Type = Message.Types.MessageType.PutValue,
        Key = ByteString.CopyFrom(key),
        Record = new Record
        {
            Key = ByteString.CopyFrom(key),
            Value = ByteString.CopyFrom(value),
            TimeReceived = timeReceived ?? DateTimeOffset.UtcNow.ToString("o")
        }
    };

    /// <summary>
    /// Build a PUT_VALUE response echoing the stored record.
    /// </summary>
    public static Message CreatePutValueResponse(Record? storedRecord = null)
    {
        var msg = new Message { Type = Message.Types.MessageType.PutValue };
        if (storedRecord != null) msg.Record = storedRecord;
        return msg;
    }

    /// <summary>
    /// Build a GET_VALUE request message.
    /// </summary>
    public static Message CreateGetValueRequest(byte[] key) => new()
    {
        Type = Message.Types.MessageType.GetValue,
        Key = ByteString.CopyFrom(key)
    };

    /// <summary>
    /// Build a GET_VALUE response with optional record and closer peers.
    /// </summary>
    public static Message CreateGetValueResponse(Record? record = null, IEnumerable<DhtNode>? closerPeers = null)
    {
        var msg = new Message { Type = Message.Types.MessageType.GetValue };
        if (record != null) msg.Record = record;
        if (closerPeers != null)
        {
            foreach (var node in closerPeers)
            {
                msg.CloserPeers.Add(ToWirePeer(node));
            }
        }
        return msg;
    }

    /// <summary>
    /// Build an ADD_PROVIDER request message.
    /// </summary>
    public static Message CreateAddProviderRequest(byte[] key, IEnumerable<DhtNode> providerPeers)
    {
        var msg = new Message
        {
            Type = Message.Types.MessageType.AddProvider,
            Key = ByteString.CopyFrom(key)
        };
        foreach (var node in providerPeers)
        {
            msg.ProviderPeers.Add(ToWirePeer(node));
        }
        return msg;
    }

    /// <summary>
    /// Build a GET_PROVIDERS request message.
    /// </summary>
    public static Message CreateGetProvidersRequest(byte[] key) => new()
    {
        Type = Message.Types.MessageType.GetProviders,
        Key = ByteString.CopyFrom(key)
    };

    /// <summary>
    /// Build a GET_PROVIDERS response with providers and closer peers.
    /// </summary>
    public static Message CreateGetProvidersResponse(
        IEnumerable<DhtNode>? providerPeers = null,
        IEnumerable<DhtNode>? closerPeers = null)
    {
        var msg = new Message { Type = Message.Types.MessageType.GetProviders };
        if (providerPeers != null)
        {
            foreach (var node in providerPeers)
            {
                msg.ProviderPeers.Add(ToWirePeer(node));
            }
        }
        if (closerPeers != null)
        {
            foreach (var node in closerPeers)
            {
                msg.CloserPeers.Add(ToWirePeer(node));
            }
        }
        return msg;
    }
}
