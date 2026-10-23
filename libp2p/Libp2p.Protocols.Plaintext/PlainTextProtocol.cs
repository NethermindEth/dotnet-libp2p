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
public class PlainTextProtocol(TimeProvider? timeProvider = null) : SymmetricProtocol, IConnectionProtocol
{
    public string Id => "/plaintext/2.0.0";

    /// <summary>
    /// The largest exchange message accepted, matching go-libp2p's limit. An
    /// exchange is one peer ID and one public key, so even a large RSA key
    /// fits with room to spare.
    /// </summary>
    private const int MaxExchangeLength = 1 << 16;

    /// <summary>
    /// How long the remote peer has to send its whole exchange, matching the
    /// limit go-libp2p puts on an inbound upgrade, security handshake included.
    /// </summary>
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

        Exchange dest = await ReadExchangeAsync(channel);
        SetRemoteIdentity(context, dest);

        await context.Upgrade(channel);
    }

    /// <remarks>
    /// The length comes from a peer that has not proven any identity yet, so it
    /// is checked before any of the message is read. One deadline covers the
    /// length and the body together, so sending the bytes slowly does not
    /// extend it. A peer that misses it fails the read like any closed channel,
    /// which is logged as a routine connection failure.
    /// </remarks>
    private async Task<Exchange> ReadExchangeAsync(IChannel channel)
    {
        using CancellationTokenSource deadline = new(ExchangeTimeout, _timeProvider);
        try
        {
            return await channel.ReadPrefixedProtobufAsync(Exchange.Parser, MaxExchangeLength, deadline.Token);
        }
        catch (InvalidDataException e)
        {
            throw new Libp2pException($"Plaintext exchange message is larger than {MaxExchangeLength} bytes.", e);
        }
        catch (Exception e) when (e is InvalidProtocolBufferException or FormatException)
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
