// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

extern alias BouncyCastleCryptography;
using Google.Protobuf;
using Nethermind.Libp2p.Protocols.GossipSub.Dto;
using BouncyCastleCryptography::Org.BouncyCastle.Math.EC.Rfc8032;
using System.Buffers.Binary;
using System.Text;

namespace Libp2p.Protocols;

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

    public static bool IsValid(this Message message, byte[] pubkey)
    {
        string msgStr = Encoding.UTF8.GetString(message.Data.ToByteArray());
        Message msgToBeVerified = message.Clone();
        msgToBeVerified.ClearSignature();

        byte[] msgToSign = Encoding.UTF8.GetBytes(SignaturePayloadPrefix)
          .Concat(msgToBeVerified.ToByteArray())
          .ToArray();
        return Ed25519.Verify(message.Signature.ToByteArray(), 0, pubkey, 0, msgToSign, 0, msgToSign.Length);
    }
}
