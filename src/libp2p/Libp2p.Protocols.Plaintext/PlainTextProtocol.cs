// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.PlainText.Dto;

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
        Exchange? dest = Exchange.Parser.ParseFrom(buf);
        if (dest?.Pubkey is not null)
        {
            PublicKey remotePublicKey = PublicKey.Parser.ParseFrom(dest.Pubkey);
            context.State.RemotePublicKey = remotePublicKey;
            PeerId remotePeerId = new(remotePublicKey);
            if (context.State.RemoteAddress is not null && context.State.RemoteAddress.GetPeerId() is PeerId expectedPeerId && expectedPeerId != remotePeerId)
            {
                throw new Libp2pException($"Plaintext remote peer id {remotePeerId} does not match address peer id {expectedPeerId}.");
            }

            if (context.State.RemoteAddress is not null && context.State.RemoteAddress.GetPeerId() is null)
            {
                context.State.RemoteAddress = context.State.RemoteAddress.Add<Multiformats.Address.Protocols.P2P>(remotePeerId.ToString());
            }
        }

        await context.Upgrade(channel);
    }
}
