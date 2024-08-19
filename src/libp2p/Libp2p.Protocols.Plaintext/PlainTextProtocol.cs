// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
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

        await context.Upgrade(channel);
    }
}

public class RelayHopProtocol : ISessionProtocol
{
    public string Id => "/libp2p/circuit/relay/0.2.0/hop";

    public Task DialAsync(IChannel downChannel, ISessionContext context)
    {
        throw new NotImplementedException();
    }

    public Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        throw new NotImplementedException();
    }
}
public class RelayStopProtocol : ISessionProtocol
{
    public string Id => "/libp2p/circuit/relay/0.2.0/stop";

    public Task DialAsync(IChannel downChannel, ISessionContext context)
    {
        throw new NotImplementedException();
    }

    public Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        throw new NotImplementedException();
    }
}
