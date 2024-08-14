// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Dto;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/tree/master/identify
/// </summary>
public class IdentifyProtocol : ISessionProtocol
{
    private readonly string _agentVersion;
    private readonly string _protocolVersion;

    private readonly ILogger? _logger;
    private readonly IPeerFactoryBuilder _peerFactoryBuilder;

    public IdentifyProtocol(IPeerFactoryBuilder peerFactoryBuilder, IdentifyProtocolSettings? settings = null, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<IdentifyProtocol>();
        _peerFactoryBuilder = peerFactoryBuilder;

        _agentVersion = settings?.AgentVersion ?? IdentifyProtocolSettings.Default.AgentVersion!;
        _protocolVersion = settings?.ProtocolVersion ?? IdentifyProtocolSettings.Default.ProtocolVersion!;
    }

    public string Id => "/ipfs/id/1.0.0";

    public async Task DialAsync(IChannel channel, ISessionContext context)
    {
        _logger?.LogInformation("Dial");

        Identify.Dto.Identify identity = await channel.ReadPrefixedProtobufAsync(Identify.Dto.Identify.Parser);

        _logger?.LogInformation("Received peer info: {identify}", identity);
        context.Remote.Identity = new Identity(PublicKey.Parser.ParseFrom(identity.PublicKey));

        if (context.Remote.Identity.PublicKey.ToByteString() != identity.PublicKey)
        {
            throw new PeerConnectionException();
        }
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        _logger?.LogInformation("Listen");

        Identify.Dto.Identify identify = new()
        {
            ProtocolVersion = _protocolVersion,
            AgentVersion = _agentVersion,
            PublicKey = context.Peer.Identity.PublicKey.ToByteString(),
            ListenAddrs = { ByteString.CopyFrom(context.Peer.Address.Get<IP>().ToBytes()) },
            ObservedAddr = ByteString.CopyFrom(context.Remote.Address!.Get<IP>().ToBytes()),
            Protocols = { _peerFactoryBuilder.AppLayerProtocols.Select(p => p.Id) }
        };
        byte[] ar = new byte[identify.CalculateSize()];
        identify.WriteTo(ar);

        await channel.WriteSizeAndDataAsync(ar);
        _logger?.LogDebug("Sent peer info {identify}", identify);
    }
}
