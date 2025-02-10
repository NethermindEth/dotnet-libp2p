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

public abstract class IdentifyProtocolBase(IProtocolStackSettings protocolStackSettings, IdentifyProtocolSettings? settings = null, PeerStore? peerStore = null, ILoggerFactory? loggerFactory = null)
{
    protected readonly ILogger? _logger = loggerFactory?.CreateLogger<IdentifyProtocol>();
    private readonly PeerStore? _peerStore = peerStore;
    private readonly IProtocolStackSettings _protocolStackSettings = protocolStackSettings;
    private readonly IdentifyProtocolSettings _settings = settings ?? new IdentifyProtocolSettings();

    protected async Task ReadAndVerifyIndentity(IChannel channel, ISessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemotePublicKey);
        ArgumentNullException.ThrowIfNull(context.State.RemotePeerId);

        Identify.Dto.Identify identify = await channel.ReadPrefixedProtobufAsync(Identify.Dto.Identify.Parser);

        _logger?.LogInformation("Received peer info: {identify}", identify);

        if (context.State.RemotePublicKey.ToByteString() != identify.PublicKey)
        {
            throw new PeerConnectionException("Malformed peer identity: the remote public key corresponds to a different peer id");
        }

        ulong seq = 0;

        if (identify.SignedPeerRecord is not null)
        {
            if (!SigningHelper.VerifyPeerRecord(identify.SignedPeerRecord, context.State.RemotePublicKey, out seq))
            {
                if (_settings?.PeerRecordsVerificationPolicy == PeerRecordsVerificationPolicy.RequireCorrect)
                {
                    throw new PeerConnectionException("Malformed peer identity: peer record signature is not valid");
                }
                else
                {
                    _logger?.LogWarning("Malformed peer identity: peer record signature is not valid");
                }
            }
            else
            {
                _logger?.LogDebug("Confirmed peer record: {peerId}", context.State.RemotePeerId);
            }
        }
        else if (_settings.PeerRecordsVerificationPolicy != PeerRecordsVerificationPolicy.DoesNotRequire)
        {
            throw new PeerConnectionException("Malformed peer identity: there is no peer record which is required");
        }

        if (_peerStore is not null)
        {
            PeerStore.PeerInfo peerInfo = _peerStore.GetPeerInfo(context.State.RemotePeerId);

            if (identify.SignedPeerRecord is not null && peerInfo.Seq >= seq)
            {
                // do nothing if seq is for an older record
                return;
            }
            peerInfo.SupportedProtocols = identify.Protocols.ToArray();
            peerInfo.SignedPeerRecord = identify.SignedPeerRecord;
            peerInfo.Seq = seq;
        }
    }

    protected async Task SendIdentity(IChannel channel, ISessionContext context, ulong idVersion = 1)
    {
        Identify.Dto.Identify identify = new()
        {
            ProtocolVersion = _settings.ProtocolVersion,
            AgentVersion = _settings.AgentVersion,
            PublicKey = context.Peer.Identity.PublicKey.ToByteString(),
            ListenAddrs = { },
            ObservedAddr = ByteString.CopyFrom(context.State.RemoteAddress!.ToEndPoint(out ProtocolType proto).ToMultiaddress(proto).ToBytes()),
            Protocols = { _protocolStackSettings.Protocols!.Select(r => r.Key.Protocol).OfType<ISessionListenerProtocol>().Select(p => p.Id) },
            SignedPeerRecord = SigningHelper.CreateSignedEnvelope(context.Peer.Identity, [.. context.Peer.ListenAddresses], idVersion),
        };

        ByteString[] endpoints = context.Peer.ListenAddresses.Where(a => !a.ToEndPoint().Address.IsPrivate()).Select(a => a.ToEndPoint(out ProtocolType proto).ToMultiaddress(proto)).Select(a => ByteString.CopyFrom(a.ToBytes())).ToArray();
        identify.ListenAddrs.AddRange(endpoints);

        await channel.WriteSizeAndProtobufAsync(identify);
        _logger?.LogDebug("Sent peer info {identify}", identify);
    }
}
