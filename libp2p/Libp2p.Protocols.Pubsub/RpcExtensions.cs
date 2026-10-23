// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
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
        if (PeerId.ExtractPublicKey(from) is null)
        {
            msg.Key = PublicKeyDecoder.Decode(identity.PublicKey.ToByteString()).ToByteString();
        }
        rpc.Publish.Add(msg);
        return rpc;
    }

    public static Rpc WithTopics(this Rpc rpc, IEnumerable<string> addTopics, IEnumerable<string> removeTopics)
    {
        IEnumerable<Rpc.Types.SubOpts> subsOpts =
            addTopics.Select(a => new Rpc.Types.SubOpts { Subscribe = true, Topicid = a }).Concat(
            removeTopics.Select(r => new Rpc.Types.SubOpts { Subscribe = false, Topicid = r })
            );
        rpc.Subscriptions.AddRange(subsOpts);
        return rpc;
    }

    public static bool VerifySignature(this Message message, PubsubSettings.SignaturePolicy signaturePolicy)
    {
        if (signaturePolicy is PubsubSettings.SignaturePolicy.StrictNoSign)
        {
            return !message.HasSignature && !message.HasFrom && !message.HasSeqno && !message.HasKey;
        }

        if (message.Signature.IsEmpty)
        {
            return false;
        }

        PublicKey? pubKey;
        try
        {
            if (message.HasKey)
            {
                pubKey = PublicKeyDecoder.Decode(message.Key);
                if (!new PeerId(pubKey).Bytes.AsSpan().SequenceEqual(message.From.Span)) return false;
            }
            else
            {
                pubKey = PeerId.ExtractPublicKey(message.From.ToByteArray());
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A malformed peer ID is an invalid wire message.
            return false;
        }
        if (pubKey is null)
        {
            return false;
        }

        Message msgToBeVerified = message.Clone();
        msgToBeVerified.ClearSignature();
        // Go and Rust sign before attaching the key. Keep all other fields,
        // including unknown fields, and never mutate the received message.
        msgToBeVerified.ClearKey();

        byte[] msgToSign = Encoding.UTF8.GetBytes(SignaturePayloadPrefix)
            .Concat(msgToBeVerified.ToByteArray())
            .ToArray();

        return new Identity(pubKey).VerifySignature(msgToSign, message.Signature.ToByteArray());
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
