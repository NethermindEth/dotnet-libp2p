// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols.Identify;
using Nethermind.Libp2p.Stack;
using System.Net.Sockets;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Identify.Dto;

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
    private readonly PeerStore? _peerStore;
    private readonly IProtocolStackSettings _protocolStackSettings;

    private static readonly byte[] Libp2pPeerRecordAsArray = [((ushort)Core.Enums.Libp2p.Libp2pPeerRecord >> 8) & 0xFF, (ushort)Core.Enums.Libp2p.Libp2pPeerRecord & 0xFF];

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
        _logger?.LogInformation("Dial");

        Identify.Dto.Identify identify = await channel.ReadPrefixedProtobufAsync(Identify.Dto.Identify.Parser);

        _logger?.LogInformation("Received peer info: {identify}", identify);
        context.RemotePeer.Identity = new Identity(PublicKey.Parser.ParseFrom(identify.PublicKey));

        if (_peerStore is not null && identify.SignedPeerRecord is not null)
        {
            if (!VerifyPeerRecord(identify.SignedPeerRecord, context.RemotePeer.Identity))
            {
                throw new PeerConnectionException();
            }
            _peerStore.GetPeerInfo(context.RemotePeer.Identity.PeerId).SignedPeerRecord = identify.SignedPeerRecord;
        }
        _logger?.LogInformation("Received peer info: {identify}", identity);
        context.State.RemotePublicKey = PublicKey.Parser.ParseFrom(identity.PublicKey);

        if (context.State.RemotePublicKey.ToByteString() != identity.PublicKey)
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
            PublicKey = context.LocalPeer.Identity.PublicKey.ToByteString(),
            ListenAddrs = { ByteString.CopyFrom(ToEndpoint(context.LocalEndpoint).ToBytes()) },
            ObservedAddr = ByteString.CopyFrom(State.RemoteAddress!.ToEndPoint(out ProtocolType proto).ToMultiaddress(proto).ToBytes()),
            Protocols = { _protocolStackSettings.Protocols!.Select(r => r.Key.Protocol).OfType<ISessionProtocol>().Select(p => p.Id) },
            SignedPeerRecord = CreateSignedEnvelope(context.LocalPeer.Identity, [context.LocalPeer.Address], 1),
        };

        ByteString[] endpoints = context.Peer.ListenAddresses.Where(a => !a.ToEndPoint().Address.IsPrivate()).Select(a => a.ToEndPoint(out ProtocolType proto).ToMultiaddress(proto)).Select(a => ByteString.CopyFrom(a.ToBytes())).ToArray();
        identify.ListenAddrs.AddRange(endpoints);

        byte[] ar = new byte[identify.CalculateSize()];
        identify.WriteTo(ar);

        await channel.WriteSizeAndDataAsync(ar);
        _logger?.LogDebug("Sent peer info {identify}", identify);
    }

    private static bool VerifyPeerRecord(ByteString signedPeerRecordBytes, Identity identity)
    {
        SignedEnvelope envelope = SignedEnvelope.Parser.ParseFrom(signedPeerRecordBytes);

        if (envelope.PayloadType?.Take(2).SequenceEqual(Libp2pPeerRecordAsArray) is not true)
        {
            return false;
        }

        PeerRecord pr = PeerRecord.Parser.ParseFrom(envelope.Payload);

        if (identity.PeerId != new PeerId(pr.PeerId.ToByteArray()))
        {
            return false;
        }

        SignedEnvelope envelopeWithoutSignature = envelope.Clone();
        envelopeWithoutSignature.ClearSignature();

        return identity.VerifySignature(envelopeWithoutSignature.ToByteArray(), envelope.Signature.ToByteArray());
    }

    private static ByteString CreateSignedEnvelope(Identity identity, Multiaddress[] addresses, ulong seq)
    {
        PeerRecord paylaod = new()
        {
            PeerId = ByteString.CopyFrom(identity.PeerId.Bytes),
            Seq = seq
        };

        foreach (var address in addresses)
        {
            paylaod.Addresses.Add(new AddressInfo
            {
                Multiaddr = ByteString.CopyFrom(address.ToBytes())
            });
        }

        SignedEnvelope envelope = new()
        {
            PayloadType = ByteString.CopyFrom(Libp2pPeerRecordAsArray),
            Payload = paylaod.ToByteString(),
            PublicKey = identity.PublicKey.ToByteString(),
        };

        envelope.Signature = ByteString.CopyFrom(identity.Sign(envelope.ToByteArray()));
        return envelope.ToByteString();
    }

    private static Multiaddress ToEndpoint(Multiaddress addr) => new()
    {
        Protocols =
            {
                addr.Has<IP4>() ? addr.Get<IP4>() : addr.Get<IP6>(),
                addr.Has<TCP>() ? addr.Get<TCP>() : addr.Get<UDP>()
            }
    };
}
