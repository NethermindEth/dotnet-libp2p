// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

extern alias BouncyCastleCryptography;
using Google.Protobuf;
using Nethermind.Libp2p.Protocols.GossipSub.Dto;
using BouncyCastleCryptography::Org.BouncyCastle.Math.EC.Rfc8032;
using System.Buffers.Binary;
using System.Text;
using Multiformats.Hash;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols;

internal static class RpcExtensions
{
    private const string SignaturePayloadPrefix = "libp2p-pubsub:";

    public static Rpc WithMessages(this Rpc rpc, string topic, ulong seqNo, byte[] from, byte[] message, byte[] privateKey)
    {

        Message msg = new();
        msg.Topic = topic;
        Span<byte> seqNoBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);
        msg.Seqno = ByteString.CopyFrom(seqNoBytes);
        msg.From = ByteString.CopyFrom(from);
        msg.Data = ByteString.CopyFrom(message);

        byte[] msgToSign = Encoding.UTF8.GetBytes(SignaturePayloadPrefix)
            .Concat(msg.ToByteArray())
            .ToArray();
        byte[] sig = new byte[64];
        Ed25519.Sign(privateKey, 0, msgToSign, 0, msgToSign.Length, sig, 0);

        msg.Signature = ByteString.CopyFrom(sig);
        rpc.Publish.Add(msg);
        return rpc;
    }

    public static Rpc WithTopics(this Rpc rpc, IEnumerable<string> addTopics, IEnumerable<string> removeTopics)
    {
        IEnumerable<Rpc.Types.SubOpts> subsOpts = addTopics.Select(a => new Rpc.Types.SubOpts { Subscribe = true, Topicid = a }).Concat(
            removeTopics.Select(r => new Rpc.Types.SubOpts { Subscribe = false, Topicid = r })
            );
        rpc.Subscriptions.AddRange(subsOpts);
        return rpc;
    }

    public static bool VerifySignature(this Message message)
    {
        Multihash multihash = Multihash.Decode(message.From.ToArray());
        if (multihash.Code != HashType.ID)
        {
            return false;
        }
        var pubKey = PublicKey.Parser.ParseFrom(multihash.Digest);
        if (pubKey.Type != KeyType.Ed25519 || multihash.Code != HashType.ID)
        {
            return false;
        }

        Message msgToBeVerified = message.Clone();
        msgToBeVerified.ClearSignature();

        byte[] msgToSign = Encoding.UTF8.GetBytes(SignaturePayloadPrefix)
          .Concat(msgToBeVerified.ToByteArray())
          .ToArray();

        return Ed25519.Verify(message.Signature.ToByteArray(), 0, pubKey.Data.ToByteArray(), 0, msgToSign, 0, msgToSign.Length);
    }

    public static MessageId GetId(this Message message)
    {
        return new(message.From.Concat(message.Seqno).ToArray());
    }
}
