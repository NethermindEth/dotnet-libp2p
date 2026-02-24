// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Net;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Identify;
using System.Net;
using System.Net.NetworkInformation;
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

    protected async Task ReadAndVerifyIdentity(IChannel channel, ISessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemotePublicKey);
        ArgumentNullException.ThrowIfNull(context.State.RemotePeerId);

        Identify.Dto.Identify identify = await channel.ReadPrefixedProtobufAsync(Identify.Dto.Identify.Parser);

        _logger?.LogInformation("Received peer info: {identify}", identify);

        if (context.State.RemotePublicKey.ToByteString() != identify.PublicKey)
        {
            throw new PeerConnectionException("Malformed peer identity: the remote public key corresponds to a different peer id");
        }

        VerifySignedPeerRecordOrThrow(identify.SignedPeerRecord, context.State.RemotePublicKey, context.State.RemotePeerId, out ulong seq);

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

            // Extract and store peer's advertised addresses from signed peer record
            if (identify.SignedPeerRecord is not null)
            {
                _peerStore.Discover(identify.SignedPeerRecord);
            }
        }
    }

    private void VerifySignedPeerRecordOrThrow(ByteString? signedPeerRecordBytes, PublicKey remotePublicKey, PeerId remotePeerId, out ulong seq)
    {
        if (signedPeerRecordBytes is not null)
        {
            if (!SigningHelper.VerifyPeerRecord(signedPeerRecordBytes, remotePublicKey, out seq))
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
                _logger?.LogDebug("Confirmed peer record: {peerId}", remotePeerId);
            }
        }
        else if (_settings.PeerRecordsVerificationPolicy != PeerRecordsVerificationPolicy.DoesNotRequire)
        {
            throw new PeerConnectionException("Malformed peer identity: there is no peer record which is required");
        }

        seq = 0;
    }

    protected async Task SendIdentity(IChannel channel, ISessionContext context, ulong idVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);
        Libp2pSetupException.ThrowIfNull(_protocolStackSettings.Protocols);

        // Allow remote peers receive routable addresses in both the signed peer record
        Multiaddress[] advertisedAddresses = ExpandWildcardAddresses(context.Peer.ListenAddresses).ToArray();

        Identify.Dto.Identify identify = new()
        {
            ProtocolVersion = _settings.ProtocolVersion,
            AgentVersion = _settings.AgentVersion,
            PublicKey = context.Peer.Identity.PublicKey.ToByteString(),
            ListenAddrs = { },
            ObservedAddr = ByteString.CopyFrom(context.State.RemoteAddress.GetEndpointPart().ToBytes()),
            Protocols = { _protocolStackSettings.Protocols.Select(r => r.Key.Protocol).OfType<ISessionListenerProtocol>().Select(p => p.Id) },
            SignedPeerRecord = SigningHelper.CreateSignedEnvelope(context.Peer.Identity, advertisedAddresses, idVersion),
        };

        // Include all routable addresses in ListenAddrs — only exclude loopback
        // and unspecified (0.0.0.0/::). Private/LAN addresses (192.168.x.x, 10.x.x.x, etc.)
        // are kept because they are routable within the local network.
        ByteString[] endpoints = advertisedAddresses
            .Where(a =>
            {
                IPAddress ip = a.ToEndPoint().Address;
                return !IPAddress.IsLoopback(ip)
                    && !ip.Equals(IPAddress.Any)
                    && !ip.Equals(IPAddress.IPv6Any);
            })
            .Select(a => a.ToEndPoint(out ProtocolType proto).ToMultiaddress(proto))
            .Select(a => ByteString.CopyFrom(a.ToBytes())).ToArray();

        identify.ListenAddrs.AddRange(endpoints);

        await channel.WriteSizeAndProtobufAsync(identify);
        _logger?.LogDebug("Sent peer info {identify}", identify);
    }

    /// <summary>
    /// Expands wildcard listen addresses (0.0.0.0, ::) to concrete network interface IPs.
    /// Non-wildcard addresses are passed through unchanged.
    /// </summary>
    internal static IEnumerable<Multiaddress> ExpandWildcardAddresses(IEnumerable<Multiaddress> addresses)
    {
        foreach (Multiaddress addr in addresses)
        {
            IPEndPoint? endpoint = null;
            ProtocolType proto = default;
            try
            {
                endpoint = addr.ToEndPoint(out proto);
            }
            catch
            {
                // Can't parse endpoint
            }

            if (endpoint is null)
            {
                yield return addr;
                continue;
            }

            if (!endpoint.Address.Equals(IPAddress.Any) && !endpoint.Address.Equals(IPAddress.IPv6Any))
            {
                yield return addr;
                continue;
            }

            bool expanded = false;
            PeerId? peerId = addr.GetPeerId();
            AddressFamily targetFamily = endpoint.AddressFamily;

            foreach (NetworkInterface iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (UnicastIPAddressInformation unicast in iface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != targetFamily) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;

                    Multiaddress newAddr = new IPEndPoint(unicast.Address, endpoint.Port).ToMultiaddress(proto);
                    if (peerId is not null)
                    {
                        newAddr = newAddr.Add<P2P>(peerId.ToString());
                    }

                    yield return newAddr;
                    expanded = true;
                }
            }

            if (!expanded)
            {
                yield return addr;
            }
        }
    }
}
