// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.PlainText.Dto;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// </summary>
public class PlainTextProtocol : SymetricProtocol, IProtocol
{
    public string Id => "/plaintext/2.0.0";

    protected override async Task ConnectAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context, bool isListener)
    {
        Exchange src = new()
        {
            Id = ByteString.CopyFrom(context.LocalPeer.Identity.PeerIdBytes),
            Pubkey = context.LocalPeer.Identity.PublicKey.ToByteString()
        };
        int size = src.CalculateSize();
        int sizeOfSize = VarInt.GetSizeInBytes(size);
        byte[] buf = new byte[size];
        src.WriteTo(buf);
        byte[] sizeBuf = new byte[sizeOfSize];
        int offset1 = 0;
        VarInt.Encode(size, sizeBuf, ref offset1);
        await channel.Writer.WriteAsync(sizeBuf.Concat(buf).ToArray());

        ulong structSize = await channel.Reader.ReadVarintAsync();
        buf = new byte[structSize];
        await channel.Reader.ReadAsync(buf);
        Exchange? dest = Exchange.Parser.ParseFrom(buf);

        _ = isListener
            ? channelFactory.SubListenAndBind(channel, context)
            : channelFactory.SubDialAndBind(channel, context);
    }
}
