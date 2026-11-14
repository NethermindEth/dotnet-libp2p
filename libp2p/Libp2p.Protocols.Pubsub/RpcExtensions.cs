// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using System.Buffers.Binary;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Google.Protobuf.Collections;

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal static class RpcExtensions
{
    private static ReadOnlySpan<byte> SignaturePayloadPrefix => "libp2p-pubsub:"u8;

    public static Rpc WithMessages(this Rpc rpc, string topic, ulong seqNo, byte[] from, byte[] message, Identity identity)
    {
        Message msg = new();
        msg.Topic = topic;
        Span<byte> seqNoBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);
        msg.Seqno = ByteString.CopyFrom(seqNoBytes);
        msg.From = ByteString.CopyFrom(from);
        msg.Data = ByteString.CopyFrom(message);

        msg.Signature = UnsafeByteOperations.UnsafeWrap(identity.Sign(GetSigningContent(msg)));
        if (PeerId.ExtractPublicKey(from) is null)
        {
            msg.Key = PublicKeyDecoder.Normalize(identity.PublicKey).ToByteString();
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

        return new Identity(pubKey).VerifySignature(GetSigningContent(msgToBeVerified), message.Signature.ToByteArray());
    }

    private static byte[] GetSigningContent(Message message)
    {
        int size = message.CalculateSize();
        byte[] content = new byte[SignaturePayloadPrefix.Length + size];
        SignaturePayloadPrefix.CopyTo(content);
        message.WriteTo(content.AsSpan(SignaturePayloadPrefix.Length));
        return content;
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
