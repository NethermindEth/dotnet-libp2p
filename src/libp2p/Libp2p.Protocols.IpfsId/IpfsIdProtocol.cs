// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Enums;
using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/tree/master/identify
/// </summary>
public class IpfsIdProtocol : IProtocol
{
    private const string SubProtocolId = "ipfs/0.1.0";

    private readonly ILogger? _logger;

    public IpfsIdProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<IpfsIdProtocol>();
    }

    public string Id => "/ipfs/id/1.0.0";

    public async Task DialAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context)
    {
        int size = await channel.Reader.ReadVarintAsync();
        byte[] identifyDto = (await channel.Reader.ReadAsync(size)).ToArray();

        string? pubKey = context.RemotePeer.Address.At(Multiaddr.P2p);
        Identify? identity = Identify.Parser.ParseFrom(identifyDto, 0, (int)size);

        context.RemotePeer.Identity = Identity.FromPublicKey(identity.PublicKey.ToByteArray());
        _logger?.LogInformation("Received peer id={0}", identity.PublicKey);
        await channel.CloseAsync();
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context)
    {
        Identify identify = new()
        {
            AgentVersion = "github.com/Nethermind/libp2p/examples/chat@1.0.0",
            ProtocolVersion = SubProtocolId,
            ListenAddrs = { ByteString.CopyFrom(context.LocalEndpoint.ToByteArray()) },
            ObservedAddr = ByteString.CopyFrom(context.RemoteEndpoint.ToByteArray()),
            PublicKey = context.LocalPeer.Identity.PublicKey.ToByteString(),
            Protocols = { context.ApplayerProtocols.Select(p => p.Id) }
        };
        byte[] ar = new byte[identify.CalculateSize()];
        identify.WriteTo(ar);
        await channel.Writer.WriteSizeAndDataAsync(ar);
        _logger?.LogInformation("Sent peer id to {0}", context.RemotePeer.Address);

        await channel.CloseAsync();
    }
}
