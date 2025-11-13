// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.RequestResponse;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>
/// Real libp2p implementation of IKademliaMessageSender that uses libp2p protocols
/// for network communication.
/// </summary>
public class LibP2pKademliaMessageSender : Kademlia.IKademliaMessageSender<PublicKey, DhtNode>
{
    private readonly ILocalPeer _localPeer;
    private readonly ILogger<LibP2pKademliaMessageSender>? _logger;
    private readonly TimeSpan _operationTimeout;

    public LibP2pKademliaMessageSender(
        ILocalPeer localPeer,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? operationTimeout = null)
    {
        _localPeer = localPeer ?? throw new ArgumentNullException(nameof(localPeer));
        _logger = loggerFactory?.CreateLogger<LibP2pKademliaMessageSender>();
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);

        _logger?.LogInformation("LibP2p Kademlia message sender initialized with timeout {Timeout}ms",
            _operationTimeout.TotalMilliseconds);
    }

    public async Task Ping(DhtNode receiver, CancellationToken token = default)
    {
        _logger?.LogDebug("Sending Ping to {NodeId}", receiver.PeerId);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            // Create ping request
            var request = new PingRequest();

            // Connect to the target node and send ping
            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            await session.DialAsync<KadDhtPingProtocol, PingRequest, PingResponse>(request, timeoutCts.Token);

            _logger?.LogTrace("Received Ping response from {NodeId}", receiver.PeerId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogDebug("Ping to {NodeId} cancelled", receiver.PeerId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Ping to {NodeId} failed: {Error}", receiver.PeerId, ex.Message);
        }
    }

    public async Task<DhtNode[]> FindNeighbours(DhtNode receiver, PublicKey target, CancellationToken token = default)
    {
        _logger?.LogDebug("Sending FindNeighbours to {NodeId} for key {SearchKey}", receiver.PeerId, Convert.ToHexString(target.Bytes[..Math.Min(8, target.Bytes.Length)]));

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            // Create find neighbours request
            var request = new FindNeighboursRequest
            {
                Target = new PublicKeyBytes
                {
                    Value = Google.Protobuf.ByteString.CopyFrom(target.Bytes)
                }
            };

            // Connect to the target node and send request
            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var response = await session.DialAsync<KadDhtFindNeighboursProtocol, FindNeighboursRequest, FindNeighboursResponse>(request, timeoutCts.Token);

            // Convert response to DhtNode array
            var nodes = response.Neighbours.Select(ConvertToDhtNode).ToArray();

            _logger?.LogDebug("Received {Count} neighbours from {NodeId}", nodes.Length, receiver.PeerId);
            return nodes;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogDebug("FindNeighbours to {NodeId} cancelled", receiver.PeerId);
            return Array.Empty<DhtNode>();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FindNeighbours to {NodeId} failed: {Error}", receiver.PeerId, ex.Message);
            return Array.Empty<DhtNode>();
        }
    }

    /// <summary>
    /// Establish a session connection to a target node using multiple strategies.
    /// </summary>
    private async Task<ISession> DialNodeAsync(DhtNode targetNode, CancellationToken cancellationToken)
    {
        _logger?.LogDebug("Attempting to dial node {NodeId} using multiple strategies", targetNode.PeerId);

        // Strategy 1: Try to find known addresses from peer store or common patterns
        var knownAddresses = await GetKnownAddressesAsync(targetNode.PeerId);

        // Strategy 2: Try direct connection with known addresses
        foreach (var address in knownAddresses)
        {
            try
            {
                _logger?.LogTrace("Trying known address {Address} for node {NodeId}", address, targetNode.PeerId);
                var session = await _localPeer.DialAsync(address, cancellationToken);
                _logger?.LogDebug("Successfully dialed node {NodeId} via {Address}", targetNode.PeerId, address);
                return session;
            }
            catch (Exception innerEx)
            {
                _logger?.LogTrace(innerEx, "Failed to dial {Address} for node {NodeId}", address, targetNode.PeerId);
            }
        }

        // Strategy 3: Fallback to basic peer ID only (for mDNS discovered peers)
        try
        {
            var basicAddress = Multiaddress.Decode($"/p2p/{targetNode.PeerId}");
            _logger?.LogTrace("Trying basic peer-only address for node {NodeId}", targetNode.PeerId);
            var session = await _localPeer.DialAsync(basicAddress, cancellationToken);
            _logger?.LogDebug("Successfully dialed node {NodeId} via basic address", targetNode.PeerId);
            return session;
        }
        catch (Exception fallbackEx)
        {
            _logger?.LogDebug(fallbackEx, "All dial strategies failed for node {NodeId}", targetNode.PeerId);
            throw new InvalidOperationException($"Unable to establish connection to node {targetNode.PeerId}", fallbackEx);
        }
    }

    /// <summary>
    /// Get known addresses for a peer using various discovery strategies.
    /// </summary>
    private async Task<IEnumerable<Multiaddress>> GetKnownAddressesAsync(PeerId peerId)
    {
        try
        {
            var addresses = new List<Multiaddress>();

            // Strategy 1: Real libp2p bootstrap node addresses
            var knownBootstrapAddresses = new Dictionary<string, string[]>
            {
                ["QmaCpDMGvV2BGHeYERUEnRQAwe3N8SzbUtfsmvsqQLuvuJ"] = new[] { "/ip4/104.131.131.82/tcp/4001" },
                ["QmNnooDu7bfjPFoTZYxMNLWUQJyrVwtbZg5gBMjTezGAJN"] = new[] { "/dnsaddr/bootstrap.libp2p.io" },
                ["QmbLHAnMoJPWSCR5Zhtx6BHJX9KiKNN6tpvbUcqanj75Nb"] = new[] { "/dnsaddr/bootstrap.libp2p.io" },
                ["QmZa1sAxajnQjVM8WjWXoMbmPd7NsWhfKsPkErzpm9wGkp"] = new[] { "/dnsaddr/bootstrap.libp2p.io" },
                ["QmQCU2EcMqAqQPR2i9bChDtGNJchTbq5TbXJJ16u19uLTa"] = new[] { "/dnsaddr/bootstrap.libp2p.io" },
                ["QmcZf59bWwK5XFi76CZX8cbJ4BhTzzA3gU1ZjYZcYW3dwt"] = new[] { "/dnsaddr/bootstrap.libp2p.io" }
            };

            // Check if this is a known bootstrap node
            if (knownBootstrapAddresses.TryGetValue(peerId.ToString(), out var bootstrapAddrs))
            {
                foreach (var addr in bootstrapAddrs)
                {
                    try
                    {
                        addresses.Add(Multiaddress.Decode($"{addr}/p2p/{peerId}"));
                    }
                    catch
                    {
                        // Ignore malformed addresses
                    }
                }
            }

            // Strategy 2: Common local network address patterns for development/testing
            var commonPorts = new[] { 4001, 4002, 4003, 8080, 9000, 9001 };
            var localIps = new[] { "127.0.0.1" };

            foreach (var ip in localIps)
            {
                foreach (var port in commonPorts)
                {
                    try
                    {
                        // TCP addresses (most common for libp2p)
                        addresses.Add(Multiaddress.Decode($"/ip4/{ip}/tcp/{port}/p2p/{peerId}"));

                        // QUIC addresses (modern transport)
                        addresses.Add(Multiaddress.Decode($"/ip4/{ip}/udp/{port}/quic-v1/p2p/{peerId}"));
                    }
                    catch
                    {
                        // Ignore malformed addresses
                    }
                }
            }

            _logger?.LogTrace("Generated {Count} candidate addresses for peer {PeerId}", addresses.Count, peerId);
            return addresses;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error generating known addresses for {PeerId}", peerId);
            return Enumerable.Empty<Multiaddress>();
        }
    }

    /// <summary>
    /// Convert protobuf Node to DhtNode.
    /// </summary>
    private static DhtNode ConvertToDhtNode(Node protobufNode)
    {
        var publicKeyBytes = protobufNode.PublicKey.ToByteArray();
        var publicKey = new PublicKey(publicKeyBytes);
        var peerId = new PeerId(publicKeyBytes); // Create PeerId from public key bytes

        return new DhtNode
        {
            PeerId = peerId,
            PublicKey = publicKey,
            Multiaddrs = protobufNode.Multiaddrs.ToArray()
        };
    }

    /// <summary>
    /// Convert DhtNode to protobuf Node.
    /// </summary>
    private static Node ConvertToProtobufNode(DhtNode dhtNode)
    {
        return new Node
        {
            PublicKey = Google.Protobuf.ByteString.CopyFrom(dhtNode.PublicKey.Bytes),
            Multiaddrs = { dhtNode.Multiaddrs }
        };
    }

    public void Dispose()
    {
        _logger?.LogDebug("LibP2p Kademlia message sender disposed");
    }
}
