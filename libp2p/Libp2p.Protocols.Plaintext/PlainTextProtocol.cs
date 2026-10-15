// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Google.Protobuf;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.PlainText.Dto;
using PublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// </summary>
public class PlainTextProtocol : SymmetricProtocol, IConnectionProtocol
{
    public string Id => "/plaintext/2.0.0";

    protected override async Task ConnectAsync(IChannel channel, IConnectionContext context, bool isListener)
    {
        Exchange src = new()
        {
            Id = ByteString.CopyFrom(context.Peer.Identity.PeerId.Bytes),
            Pubkey = context.Peer.Identity.PublicKey.ToByteString()
        };
        int size = src.CalculateSize();
        int sizeOfSize = VarInt.GetSizeInBytes(size);
        byte[] buf = new byte[size];
        src.WriteTo(buf);
        byte[] sizeBuf = new byte[sizeOfSize];
        int offset1 = 0;
        VarInt.Encode(size, sizeBuf, ref offset1);
        await channel.WriteAsync(new ReadOnlySequence<byte>(sizeBuf.Concat(buf).ToArray()));

        int structSize = await channel.ReadVarintAsync();
        buf = (await channel.ReadAsync(structSize).OrThrow()).ToArray();
        Exchange dest = ParseExchange(buf);
        SetRemoteIdentity(context, dest);

        await context.Upgrade(channel);
    }

    private static Exchange ParseExchange(byte[] buf)
    {
        try
        {
            return Exchange.Parser.ParseFrom(buf);
        }
        catch (InvalidProtocolBufferException e)
        {
            throw new Libp2pException("Plaintext exchange message is malformed.", e);
        }
    }

    /// <remarks>
    /// https://github.com/libp2p/specs/blob/master/plaintext/README.md#exchange: both sides derive the peer ID
    /// from the exchanged public key and check it against the advertised one; the dialer additionally checks it
    /// against the peer ID it meant to dial. Any mismatch terminates the connection.
    /// </remarks>
    private static void SetRemoteIdentity(IConnectionContext context, Exchange remote)
    {
        if (!remote.HasPubkey)
        {
            throw new Libp2pException("Plaintext exchange does not contain a public key.");
        }

        PublicKey remotePublicKey = PublicKeyDecoder.Decode(remote.Pubkey);
        PeerId remotePeerId = new(remotePublicKey);

        if (!remote.HasId || !remote.Id.Span.SequenceEqual(remotePeerId.Bytes))
        {
            throw new Libp2pException("Plaintext exchange peer ID does not match the exchanged public key.");
        }

        if (context.State.RemotePublicKey is { } existingRemotePublicKey && existingRemotePublicKey.ToByteString() != remotePublicKey.ToByteString())
        {
            throw new Libp2pException("Plaintext identity does not match the previously authenticated remote public key.");
        }

        PeerId? expectedPeerId = context.State.RemoteAddress?.GetPeerId();
        if (expectedPeerId is not null && expectedPeerId != remotePeerId)
        {
            throw new Libp2pException("Plaintext exchange identity does not match the expected remote peer ID.");
        }

        context.State.RemotePublicKey = remotePublicKey;
        if (context.State.RemoteAddress is { } remoteAddress && expectedPeerId is null)
        {
            remoteAddress.Add(new P2P(remotePeerId.ToString()));
        }
    }
}
