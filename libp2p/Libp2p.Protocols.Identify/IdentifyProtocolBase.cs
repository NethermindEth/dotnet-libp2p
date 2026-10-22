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
using System.Text;

namespace Nethermind.Libp2p.Protocols;

public abstract class IdentifyProtocolBase(IProtocolStackSettings protocolStackSettings, IdentifyProtocolSettings? settings = null, PeerStore? peerStore = null, ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
{
    // Match go-libp2p's signed identify message cap and read deadline, for both Identify and Push.
    private const int MaxMessageLength = 8 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    // Bound retained metadata independently of the Identify wire message limit.
    private const int DefaultProtocolCapacity = 64;
    private const int MaxStoredProtocolIdBytes = 128;
    protected readonly ILogger? _logger = loggerFactory?.CreateLogger<IdentifyProtocol>();
    private readonly PeerStore? _peerStore = peerStore;
    private readonly IProtocolStackSettings _protocolStackSettings = protocolStackSettings;
    private readonly IdentifyProtocolSettings _settings = settings ?? new IdentifyProtocolSettings();

    protected Task<IdentifyResult> ReadAndVerifyIdentity(IChannel channel, ISessionContext context) => ReadAndVerifyIdentity(channel, context, isPush: false);

    protected async Task<IdentifyResult> ReadAndVerifyIdentity(IChannel channel, ISessionContext context, bool isPush)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemotePublicKey);
        ArgumentNullException.ThrowIfNull(context.State.RemotePeerId);

        using CancellationTokenSource timeout = new(ReadTimeout, _timeProvider);
        Identify.Dto.Identify identify = await channel.ReadPrefixedProtobufAsync(Identify.Dto.Identify.Parser, MaxMessageLength, timeout.Token);

        _logger?.LogInformation("Received peer info: {identify}", identify);

        if ((!isPush || identify.HasPublicKey) && !IsEncodingOf(identify.PublicKey, context.State.RemotePublicKey))
        {
            throw new PeerConnectionException("Malformed peer identity: the remote public key corresponds to a different peer id");
        }

        ByteString? signedPeerRecord = identify.HasSignedPeerRecord ? identify.SignedPeerRecord : null;
        ulong? priorSeq = context.State.VerifiedPeerRecordSequence;
        if (_peerStore?.GetVerifiedPeerRecordSequence(context.State.RemotePeerId) is { } cachedSeq)
        {
            if (priorSeq is null || cachedSeq > priorSeq) priorSeq = cachedSeq;
        }
        bool mayOmitPeerRecord = isPush && signedPeerRecord is null &&
            (_settings.PeerRecordsVerificationPolicy != PeerRecordsVerificationPolicy.RequireCorrect || priorSeq is not null);
        bool verifiedPeerRecord = VerifySignedPeerRecordOrThrow(signedPeerRecord, context.State.RemotePublicKey, context.State.RemotePeerId, mayOmitPeerRecord, out ulong seq);
        if (priorSeq is { } prior)
        {
            context.State.RecordVerifiedPeerRecordSequence(prior);
        }
        if (verifiedPeerRecord && _peerStore is null) context.State.RecordVerifiedPeerRecordSequence(seq);

        if (_peerStore is not null)
        {
            if (!isPush || identify.HasAgentVersion)
            {
                _peerStore.SetAgentVersion(context.State.RemotePeerId, identify.HasAgentVersion ? identify.AgentVersion : null);
            }

            // Repeated fields have no presence bit, so an empty Push must not erase known protocols.
            if (!isPush || identify.Protocols.Count > 0)
            {
                List<string> protocols = new(DefaultProtocolCapacity);
                HashSet<string> selected = new(StringComparer.Ordinal);
                foreach (ProtocolRef protocol in _protocolStackSettings.Protocols?.Keys.AsEnumerable() ?? [])
                {
                    if (protocol.Protocol is ISessionListenerProtocol && identify.Protocols.Contains(protocol.Id) && selected.Add(protocol.Id))
                    {
                        protocols.Add(protocol.Id);
                    }
                }
                foreach (string protocol in identify.Protocols)
                {
                    if (protocols.Count >= DefaultProtocolCapacity) break;
                    if (protocol.Length <= MaxStoredProtocolIdBytes && Encoding.UTF8.GetByteCount(protocol) <= MaxStoredProtocolIdBytes && selected.Add(protocol)) protocols.Add(protocol);
                }
                _peerStore.SetSupportedProtocols(context.State.RemotePeerId, protocols.ToArray());
            }
            if (verifiedPeerRecord)
            {
                _peerStore.Discover(signedPeerRecord!, context.State);
            }
        }
        return new IdentifyResult
        {
            PeerId = context.State.RemotePeerId,
            AgentVersion = identify.HasAgentVersion ? identify.AgentVersion : null,
            ProtocolVersion = identify.HasProtocolVersion ? identify.ProtocolVersion : null,
            Protocols = Array.AsReadOnly(identify.Protocols.ToArray()),
            ListenAddresses = Array.AsReadOnly(identify.ListenAddrs.ToArray()),
            ObservedAddress = identify.HasObservedAddr ? identify.ObservedAddr : null,
            SignedPeerRecord = verifiedPeerRecord ? signedPeerRecord : null,
            PeerRecordSequence = verifiedPeerRecord ? seq : null
        };
    }

    /// <remarks>
    /// The security handshake stored the remote key in decoded form, so the announced
    /// key is decoded before comparing; otherwise the same secp256k1 key sent in another
    /// encoding would not match. One that does not decode does not match either.
    /// </remarks>
    private static bool IsEncodingOf(ByteString bytes, PublicKey publicKey)
    {
        try
        {
            return PublicKeyDecoder.Decode(bytes).Equals(publicKey);
        }
        catch (Libp2pException)
        {
            return false;
        }
    }

    private bool VerifySignedPeerRecordOrThrow(ByteString? signedPeerRecordBytes, PublicKey remotePublicKey, PeerId remotePeerId, bool mayOmitPeerRecord, out ulong seq)
    {
        if (signedPeerRecordBytes is not null)
        {
            bool verified;
            try
            {
                verified = SigningHelper.VerifyPeerRecord(signedPeerRecordBytes, remotePublicKey, out seq);
            }
            catch (InvalidProtocolBufferException)
            {
                seq = 0;
                verified = false;
            }
            if (!verified)
            {
                if (_settings?.PeerRecordsVerificationPolicy == PeerRecordsVerificationPolicy.RequireCorrect)
                {
                    throw new PeerConnectionException("Malformed peer identity: peer record signature is not valid");
                }
                _logger?.LogWarning("Malformed peer identity: peer record signature is not valid");
                return false;
            }
            _logger?.LogDebug("Confirmed peer record: {peerId}", remotePeerId);
            return true;
        }

        seq = 0;
        if (mayOmitPeerRecord)
        {
            return false;
        }
        if (_settings.PeerRecordsVerificationPolicy == PeerRecordsVerificationPolicy.RequireCorrect)
        {
            throw new PeerConnectionException("Malformed peer identity: there is no peer record which is required");
        }
        if (_settings.PeerRecordsVerificationPolicy == PeerRecordsVerificationPolicy.RequireWithWarning)
        {
            _logger?.LogWarning("Peer identity has no signed peer record");
        }
        return false;
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
            Protocols = { _protocolStackSettings.Protocols.Keys.Where(r => r.IsExposed && r.Protocol is ISessionListenerProtocol).Select(r => r.Id).Distinct() },
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
