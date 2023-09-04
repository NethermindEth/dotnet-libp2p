// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Buffers.Binary;
using System.Text;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Google.Protobuf.Collections;

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal static class RpcExtensions
{
    private const string SignaturePayloadPrefix = "libp2p-pubsub:";

    public static Rpc WithMessages(this Rpc rpc, string topic, ulong seqNo, byte[] from, byte[] message, Identity identity)
    {
        Message msg = new();
        msg.Topic = topic;
        Span<byte> seqNoBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);
        msg.Seqno = ByteString.CopyFrom(seqNoBytes);
        msg.From = ByteString.CopyFrom(from);
        msg.Data = ByteString.CopyFrom(message);

        byte[] signingContent = Encoding.UTF8.GetBytes(SignaturePayloadPrefix)
            .Concat(msg.ToByteArray())
            .ToArray();

        msg.Signature = ByteString.CopyFrom(identity.Sign(signingContent));
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
        PublicKey? pubKey = PeerId.ExtractPublicKey(message.From.ToArray());
        if (pubKey is null)
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
    public static T Ensure<T>(this Rpc self, Func<Rpc, T> accessor)
    {
        switch (accessor)
        {
            case Func<Rpc, ControlMessage> _:
            case Func<Rpc, RepeatedField<ControlPrune>> _:
            case Func<Rpc, RepeatedField<ControlGraft>> _:
            case Func<Rpc, RepeatedField<ControlIHave>> _:
            case Func<Rpc, RepeatedField<ControlIWant>> _:
                self.Control ??= new ControlMessage();
                break;
            default:
                throw new NotImplementedException($"No {nameof(Ensure)} for {nameof(T)}");
        }
        return accessor(self);
    }
}
