// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;

namespace Libp2p.Protocols.KadDht.Kademlia;

public class TestNode
{
    public PublicKey Id { get; init; } = default!;

    /// <summary>
    /// The multiaddress of the node for real network connectivity.
    /// Should be in format: /ip4/HOST/tcp/PORT/p2p/PEERID
    /// </summary>
    public Multiaddress? Multiaddress { get; init; }

    /// <summary>
    /// Creates a TestNode with a valid multiaddress for network operations.
    /// Uses a base58-encoded representation of the public key hash as peer ID.
    /// </summary>
    /// <param name="publicKey">The public key identity of the node</param>
    /// <param name="host">The IP address (default: 127.0.0.1 for localhost)</param>
    /// <param name="port">The TCP port (default: random port 4000-8000)</param>
    /// <returns>TestNode with valid multiaddress</returns>
    public static TestNode WithNetworkAddress(PublicKey publicKey, string host = "127.0.0.1", int? port = null)
    {
        // Generate a random port if not specified
        var tcpPort = port ?? Random.Shared.Next(4000, 8000);

        // Create a proper libp2p PeerId from the Kademlia PublicKey
        // Convert Kademlia PublicKey to libp2p PublicKey format
        var keyBytes = publicKey.Bytes.ToArray();
        var libp2pPublicKey = new Nethermind.Libp2p.Core.Dto.PublicKey
        {
            Type = KeyType.Ed25519,
            Data = Google.Protobuf.ByteString.CopyFrom(keyBytes)
        };

        var peerId = new PeerId(libp2pPublicKey);

        // Create valid libp2p multiaddress
        var multiaddress = Multiformats.Address.Multiaddress.Decode($"/ip4/{host}/tcp/{tcpPort}/p2p/{peerId}");

        return new TestNode
        {
            Id = publicKey,
            Multiaddress = multiaddress
        };
    }

    /// <summary>
    /// Creates a TestNode for simulation mode (no multiaddress needed).
    /// </summary>
    /// <param name="publicKey">The public key identity of the node</param>
    /// <returns>TestNode for simulation use</returns>
    public static TestNode ForSimulation(PublicKey publicKey)
    {
        return new TestNode
        {
            Id = publicKey,
            Multiaddress = null
        };
    }
}
