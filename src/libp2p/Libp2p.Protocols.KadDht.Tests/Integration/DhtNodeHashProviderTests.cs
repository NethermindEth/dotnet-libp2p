// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;
using Nethermind.Libp2p.Core;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration
{
    [TestFixture]
    public class DhtNodeHashProviderTests
    {
        private DhtNodeHashProvider _hashProvider;
        private DhtNode _node;
        private PublicKey _publicKey;

        [SetUp]
        public void Setup()
        {
            _hashProvider = new DhtNodeHashProvider();

            var peerId = new PeerId(new byte[32]);
            _publicKey = new PublicKey(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });

            _node = new DhtNode
            {
                PeerId = peerId,
                PublicKey = _publicKey
            };
        }

        [Test]
        public void GetHash_ShouldReturnConsistentHash()
        {
            // Act
            ValueHash256 hash1 = _hashProvider.GetHash(_node);
            ValueHash256 hash2 = _hashProvider.GetHash(_node);

            // Assert
            Assert.That(hash1.Bytes.ToArray(), Is.EqualTo(hash2.Bytes.ToArray()));
        }

        [Test]
        public void GetHash_WithNullNode_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _hashProvider.GetHash(null!));
        }

        [Test]
        public void GetHash_ShouldReturn32ByteHash()
        {
            // Act
            ValueHash256 hash = _hashProvider.GetHash(_node);

            // Assert
            Assert.That(hash.Bytes.Length, Is.EqualTo(32));
        }

        [Test]
        public void GetHash_WithDifferentNodes_ShouldReturnDifferentHashes()
        {
            // Arrange
            var peerId1 = new PeerId(new byte[32]);
            var publicKey1 = new PublicKey(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
            var node1 = new DhtNode { PeerId = peerId1, PublicKey = publicKey1 };

            var peerId2 = new PeerId(new byte[32]);
            var publicKey2 = new PublicKey(new byte[32] { 32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 });
            var node2 = new DhtNode { PeerId = peerId2, PublicKey = publicKey2 };

            // Act
            ValueHash256 hash1 = _hashProvider.GetHash(node1);
            ValueHash256 hash2 = _hashProvider.GetHash(node2);

            // Assert
            Assert.That(hash1.Bytes.ToArray(), Is.Not.EqualTo(hash2.Bytes.ToArray()));
        }

        [Test]
        public void GetHash_ShouldMatchPublicKeyHash()
        {
            // Act
            ValueHash256 nodeHash = _hashProvider.GetHash(_node);
            ValueHash256 publicKeyHash = _publicKey.Hash;

            // Assert
            Assert.That(nodeHash.Bytes.ToArray(), Is.EqualTo(publicKeyHash.Bytes.ToArray()));
        }

        [Test]
        public void GetHash_WithSamePublicKeyDifferentPeerId_ShouldReturnSameHash()
        {
            // Arrange
            var peerId1 = new PeerId(new byte[32] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 });
            var peerId2 = new PeerId(new byte[32] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 });

            var node1 = new DhtNode { PeerId = peerId1, PublicKey = _publicKey };
            var node2 = new DhtNode { PeerId = peerId2, PublicKey = _publicKey };

            // Act
            ValueHash256 hash1 = _hashProvider.GetHash(node1);
            ValueHash256 hash2 = _hashProvider.GetHash(node2);

            // Assert - Hash is based on public key, not peer ID
            Assert.That(hash1.Bytes.ToArray(), Is.EqualTo(hash2.Bytes.ToArray()));
        }
    }
}
