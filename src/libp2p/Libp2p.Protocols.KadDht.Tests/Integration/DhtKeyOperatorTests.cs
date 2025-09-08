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
    public class DhtKeyOperatorTests
    {
        private DhtKeyOperator _keyOperator;
        private DhtNode _node;
        private PublicKey _publicKey;

        [SetUp]
        public void Setup()
        {
            _keyOperator = new DhtKeyOperator();
            
            var peerId = new PeerId(new byte[32]);
            _publicKey = new PublicKey(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
            
            _node = new DhtNode
            {
                PeerId = peerId,
                PublicKey = _publicKey
            };
        }

        [Test]
        public void GetKey_ShouldReturnNodePublicKey()
        {
            // Act
            PublicKey result = _keyOperator.GetKey(_node);

            // Assert
            Assert.That(result, Is.EqualTo(_publicKey));
        }

        [Test]
        public void GetKey_WithNullNode_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _keyOperator.GetKey(null!));
        }

        [Test]
        public void GetKeyHash_ShouldReturnConsistentHash()
        {
            // Act
            ValueHash256 hash1 = _keyOperator.GetKeyHash(_publicKey);
            ValueHash256 hash2 = _keyOperator.GetKeyHash(_publicKey);

            // Assert
            Assert.That(hash1.Bytes.ToArray(), Is.EqualTo(hash2.Bytes.ToArray()));
        }

        [Test]
        public void GetKeyHash_WithNullKey_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _keyOperator.GetKeyHash(null!));
        }

        [Test]
        public void GetNodeHash_ShouldReturnSameAsGetKeyHashOfPublicKey()
        {
            // Act
            ValueHash256 nodeHash = _keyOperator.GetNodeHash(_node);
            ValueHash256 keyHash = _keyOperator.GetKeyHash(_publicKey);

            // Assert
            Assert.That(nodeHash.Bytes.ToArray(), Is.EqualTo(keyHash.Bytes.ToArray()));
        }

        [Test]
        public void GetNodeHash_WithNullNode_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _keyOperator.GetNodeHash(null!));
        }

        [Test]
        public void GetKeyHash_WithDifferentKeys_ShouldReturnDifferentHashes()
        {
            // Arrange
            var publicKey1 = new PublicKey(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
            var publicKey2 = new PublicKey(new byte[32] { 32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 });

            // Act
            ValueHash256 hash1 = _keyOperator.GetKeyHash(publicKey1);
            ValueHash256 hash2 = _keyOperator.GetKeyHash(publicKey2);

            // Assert
            Assert.That(hash1.Bytes.ToArray(), Is.Not.EqualTo(hash2.Bytes.ToArray()));
        }

        [Test]
        public void GetKeyHash_ShouldReturn32ByteHash()
        {
            // Act
            ValueHash256 hash = _keyOperator.GetKeyHash(_publicKey);

            // Assert
            Assert.That(hash.Bytes.Length, Is.EqualTo(32));
        }
    }
}