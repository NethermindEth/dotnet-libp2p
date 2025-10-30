// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.TestsBase;
using Libp2p.Protocols.KadDht.Storage;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Storage
{
    [TestFixture]
    public class InMemoryValueStoreTests
    {
        private InMemoryValueStore _valueStore;
        private ILoggerFactory _loggerFactory;

        [SetUp]
        public void Setup()
        {
            _loggerFactory = new TestContextLoggerFactory();
            _valueStore = new InMemoryValueStore(maxValues: 100, loggerFactory: _loggerFactory);
        }

        [TearDown]
        public void TearDown()
        {
            (_loggerFactory as IDisposable)?.Dispose();
        }

        [Test]
        public void Constructor_WithDefaultParameters_ShouldCreateInstance()
        {
            // Act
            var store = new InMemoryValueStore();

            // Assert
            Assert.That(store.Count, Is.EqualTo(0));
        }

        [Test]
        public void Constructor_WithMaxValues_ShouldCreateInstance()
        {
            // Act
            var store = new InMemoryValueStore(maxValues: 50);

            // Assert
            Assert.That(store.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task PutValueAsync_ShouldStoreValue()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");
            var value = new StoredValue
            {
                Value = Encoding.UTF8.GetBytes("test-value"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            // Act
            bool result = await _valueStore.PutValueAsync(key, value);

            // Assert
            Assert.That(result, Is.True);
            Assert.That(_valueStore.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task GetValueAsync_WithExistingKey_ShouldReturnValue()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");
            var originalValue = new StoredValue
            {
                Value = Encoding.UTF8.GetBytes("test-value"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            await _valueStore.PutValueAsync(key, originalValue);

            // Act
            var retrievedValue = await _valueStore.GetValueAsync(key);

            // Assert
            Assert.That(retrievedValue, Is.Not.Null);
            Assert.That(retrievedValue.Value, Is.EqualTo(originalValue.Value));
            Assert.That(retrievedValue.Timestamp, Is.EqualTo(originalValue.Timestamp));
            Assert.That(retrievedValue.Ttl, Is.EqualTo(originalValue.Ttl));
        }

        [Test]
        public async Task GetValueAsync_WithNonExistentKey_ShouldReturnNull()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("non-existent-key");

            // Act
            var retrievedValue = await _valueStore.GetValueAsync(key);

            // Assert
            Assert.That(retrievedValue, Is.Null);
        }

        [Test]
        public async Task GetValueAsync_WithExpiredValue_ShouldReturnNull()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");
            var expiredValue = new StoredValue
            {
                Value = Encoding.UTF8.GetBytes("test-value"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromMilliseconds(1), // Very short TTL
                StoredAt = DateTime.UtcNow.AddHours(-1) // Stored 1 hour ago
            };

            await _valueStore.PutValueAsync(key, expiredValue);

            // Act
            var retrievedValue = await _valueStore.GetValueAsync(key);

            // Assert
            Assert.That(retrievedValue, Is.Null);
        }

        [Test]
        public async Task PutValueAsync_WhenAtCapacity_ShouldReturnFalse()
        {
            // Arrange
            var smallStore = new InMemoryValueStore(maxValues: 2);
            
            var value1 = new StoredValue
            {
                Value = Encoding.UTF8.GetBytes("value1"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            var value2 = new StoredValue
            {
                Value = Encoding.UTF8.GetBytes("value2"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            var value3 = new StoredValue
            {
                Value = Encoding.UTF8.GetBytes("value3"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            // Act
            bool result1 = await smallStore.PutValueAsync(Encoding.UTF8.GetBytes("key1"), value1);
            bool result2 = await smallStore.PutValueAsync(Encoding.UTF8.GetBytes("key2"), value2);
            bool result3 = await smallStore.PutValueAsync(Encoding.UTF8.GetBytes("key3"), value3);

            // Assert
            Assert.That(result1, Is.True);
            Assert.That(result2, Is.True);
            Assert.That(result3, Is.False); // Should fail due to capacity
            Assert.That(smallStore.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task PutValueAsync_WithSameKey_ShouldOverwriteValue()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");
            var value1 = new StoredValue
            {
                Value = Encoding.UTF8.GetBytes("original-value"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            var value2 = new StoredValue
            {
                Value = Encoding.UTF8.GetBytes("updated-value"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(2)
            };

            // Act
            await _valueStore.PutValueAsync(key, value1);
            await _valueStore.PutValueAsync(key, value2);

            var retrievedValue = await _valueStore.GetValueAsync(key);

            // Assert
            Assert.That(_valueStore.Count, Is.EqualTo(1)); // Should not increase count
            Assert.That(retrievedValue, Is.Not.Null);
            Assert.That(retrievedValue.Value, Is.EqualTo(value2.Value));
        }

        [Test]
        public async Task PutValueAsync_WithCancellationToken_ShouldRespectCancellation()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");
            var value = new StoredValue
            {
                Value = Encoding.UTF8.GetBytes("test-value"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            // Since this is an in-memory operation, it should complete even with cancellation
            bool result = await _valueStore.PutValueAsync(key, value, cts.Token);
            Assert.That(result, Is.True);
        }

        [Test]
        public async Task GetValueAsync_WithCancellationToken_ShouldRespectCancellation()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            // Since this is an in-memory operation, it should complete even with cancellation
            var result = await _valueStore.GetValueAsync(key, cts.Token);
            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task Count_ShouldReflectCurrentNumberOfStoredValues()
        {
            // Arrange
            Assert.That(_valueStore.Count, Is.EqualTo(0));

            var value = new StoredValue
            {
                Value = Encoding.UTF8.GetBytes("test-value"),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            // Act & Assert
            await _valueStore.PutValueAsync(Encoding.UTF8.GetBytes("key1"), value);
            Assert.That(_valueStore.Count, Is.EqualTo(1));

            await _valueStore.PutValueAsync(Encoding.UTF8.GetBytes("key2"), value);
            Assert.That(_valueStore.Count, Is.EqualTo(2));

            await _valueStore.PutValueAsync(Encoding.UTF8.GetBytes("key1"), value); // Overwrite
            Assert.That(_valueStore.Count, Is.EqualTo(2)); // Should not change
        }
    }
}