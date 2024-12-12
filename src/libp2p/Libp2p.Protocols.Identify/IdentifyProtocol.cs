// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Identify;
using System.Net.Sockets;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/tree/master/identify
/// </summary>
public class IdentifyProtocol : ISessionProtocol
{
    private readonly string _agentVersion;
    private readonly string _protocolVersion;

    private readonly ILogger? _logger;
    private readonly PeerStore? _peerStore;
    private readonly IProtocolStackSettings _protocolStackSettings;


    public string Id => "/ipfs/id/1.0.0";

    public IdentifyProtocol(IProtocolStackSettings protocolStackSettings, IdentifyProtocolSettings? settings = null, PeerStore? peerStore = null, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<IdentifyProtocol>();
        _peerStore = peerStore;
        _protocolStackSettings = protocolStackSettings;

        _agentVersion = settings?.AgentVersion ?? IdentifyProtocolSettings.Default.AgentVersion!;
        _protocolVersion = settings?.ProtocolVersion ?? IdentifyProtocolSettings.Default.ProtocolVersion!;
    }


    public async Task DialAsync(IChannel channel, ISessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemotePublicKey);
        ArgumentNullException.ThrowIfNull(context.State.RemotePeerId);

        _logger?.LogInformation("Dial");

        Identify.Dto.Identify identify = await channel.ReadPrefixedProtobufAsync(Identify.Dto.Identify.Parser);

        _logger?.LogInformation("Received peer info: {identify}", identify);

        if (_peerStore is not null)
        {
            _peerStore.GetPeerInfo(context.State.RemotePeerId).SupportedProtocols = identify.Protocols.ToArray();

            if (identify.SignedPeerRecord is not null)
            {
                if (!SigningHelper.VerifyPeerRecord(identify.SignedPeerRecord, context.State.RemotePublicKey))
                {
                    //throw new PeerConnectionException();
                    _logger?.LogError("Peer record is not valid: {peerId}", context.State.RemotePeerId);
                }
                else
                {
                    _peerStore.GetPeerInfo(context.State.RemotePeerId).SignedPeerRecord = identify.SignedPeerRecord;
                    _logger?.LogInformation("Confirmed peer record: {peerId}", context.State.RemotePeerId);
                }
            }
        }

        if (context.State.RemotePublicKey.ToByteString() != identify.PublicKey)
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
            ListenAddrs = { context.Peer.ListenAddresses.Select(x => ByteString.CopyFrom(x.ToBytes())) },
            ObservedAddr = ByteString.CopyFrom(context.State.RemoteAddress!.ToEndPoint(out ProtocolType proto).ToMultiaddress(proto).ToBytes()),
            Protocols = { _protocolStackSettings.Protocols!.Select(r => r.Key.Protocol).OfType<ISessionProtocol>().Select(p => p.Id) },
            SignedPeerRecord = SigningHelper.CreateSignedEnvelope(context.Peer.Identity, [.. context.Peer.ListenAddresses], 1),
        };

        ByteString[] endpoints = context.Peer.ListenAddresses.Where(a => !a.ToEndPoint().Address.IsPrivate()).Select(a => a.ToEndPoint(out ProtocolType proto).ToMultiaddress(proto)).Select(a => ByteString.CopyFrom(a.ToBytes())).ToArray();
        identify.ListenAddrs.AddRange(endpoints);

        await channel.WriteSizeAndProtobufAsync(identify);
        _logger?.LogDebug("Sent peer info {identify}", identify);
    }
}
