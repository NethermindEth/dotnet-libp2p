// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols.PlainText.Dto;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// </summary>
public class NoiseProtocol : IProtocol
{
    public string Id => "/noise";
    public async Task DialAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
    {
        //await downChannel.WriteAsync();
        var data = await downChannel.ReadAsync(0, ReadBlockingMode.WaitAny);
    }

    public async Task ListenAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
    {
        throw new NotImplementedException();
    }

    // protected override async Task ConnectAsync(IChannel downChannel, IChannelFactory channelFactory,
    //     IPeerContext context, bool isListener)
    // {
    //     Exchange src = new()
    //     {
    //         Id = ByteString.CopyFrom(context.LocalPeer.Identity.PeerIdBytes),
    //         Pubkey = context.LocalPeer.Identity.PublicKey.ToByteString()
    //     };
    //     int size = src.CalculateSize();
    //     int sizeOfSize = VarInt.GetSizeInBytes(size);
    //     byte[] buf = new byte[size];
    //     src.WriteTo(buf);
    //     byte[] sizeBuf = new byte[sizeOfSize];
    //     int offset1 = 0;
    //     VarInt.Encode(size, sizeBuf, ref offset1);
    //     await downChannel.WriteAsync(new ReadOnlySequence<byte>(sizeBuf.Concat(buf).ToArray()));
    //
    //     int structSize = await downChannel.ReadVarintAsync();
    //     buf = (await downChannel.ReadAsync(structSize)).ToArray();
    //     Exchange? dest = Exchange.Parser.ParseFrom(buf);
    //
    //     await (isListener
    //         ? channelFactory.SubListenAndBind(downChannel, context)
    //         : channelFactory.SubDialAndBind(downChannel, context));
    // }
}
