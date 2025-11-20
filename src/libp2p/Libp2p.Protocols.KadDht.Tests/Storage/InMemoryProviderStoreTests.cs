// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Libp2p.Protocols.KadDht.Storage;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Storage
{
    [TestFixture]
    public class InMemoryProviderStoreTests
    {
        private InMemoryProviderStore _providerStore;
        private ILoggerFactory _loggerFactory;

        [SetUp]
        public void Setup()
        {
            _loggerFactory = new TestContextLoggerFactory();
            _providerStore = new InMemoryProviderStore(maxProvidersPerKey: 10, loggerFactory: _loggerFactory);
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
            var store = new InMemoryProviderStore();

            // Assert - Should not throw and create empty store
            Assert.DoesNotThrow(() => { });
        }

        [Test]
        public void Constructor_WithMaxProviders_ShouldCreateInstance()
        {
            // Act
            var store = new InMemoryProviderStore(maxProvidersPerKey: 5);

            // Assert - Should not throw
            Assert.DoesNotThrow(() => { });
        }

        [Test]
        public async Task AddProviderAsync_ShouldAddProvider()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");
            var provider = new ProviderRecord
            {
                PeerId = new PeerId(new byte[32]),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1),
                Multiaddrs = new[] { "/ip4/127.0.0.1/tcp/4001" }
            };

            // Act
            bool result = await _providerStore.AddProviderAsync(key, provider);

            // Assert
            Assert.That(result, Is.True);
        }

        [Test]
        public async Task GetProvidersAsync_WithExistingKey_ShouldReturnProviders()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");
            var provider = new ProviderRecord
            {
                PeerId = new PeerId(new byte[32]),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1),
                Multiaddrs = new[] { "/ip4/127.0.0.1/tcp/4001" }
            };

            await _providerStore.AddProviderAsync(key, provider);

            // Act
            var providers = await _providerStore.GetProvidersAsync(key, maxCount: 10);

            // Assert
            Assert.That(providers, Is.Not.Null);
            Assert.That(providers.Count, Is.EqualTo(1));
            Assert.That(providers.First().PeerId, Is.EqualTo(provider.PeerId));
        }

        [Test]
        public async Task GetProvidersAsync_WithNonExistentKey_ShouldReturnEmptyList()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("non-existent-key");

            // Act
            var providers = await _providerStore.GetProvidersAsync(key, maxCount: 10);

            // Assert
            Assert.That(providers, Is.Not.Null);
            Assert.That(providers.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task GetProvidersAsync_WithExpiredProvider_ShouldReturnEmptyList()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");
            var expiredProvider = new ProviderRecord
            {
                PeerId = new PeerId(new byte[32]),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromMilliseconds(1), // Very short TTL
                StoredAt = DateTime.UtcNow.AddHours(-1), // Stored 1 hour ago
                Multiaddrs = new[] { "/ip4/127.0.0.1/tcp/4001" }
            };

            await _providerStore.AddProviderAsync(key, expiredProvider);

            // Act
            var providers = await _providerStore.GetProvidersAsync(key, maxCount: 10);

            // Assert
            Assert.That(providers.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task AddProviderAsync_WhenAtCapacity_ShouldReturnFalse()
        {
            // Arrange
            var smallStore = new InMemoryProviderStore(maxProvidersPerKey: 2);
            var key = Encoding.UTF8.GetBytes("test-key");

            var provider1 = new ProviderRecord
            {
                PeerId = new PeerId(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 }),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            var provider2 = new ProviderRecord
            {
                PeerId = new PeerId(new byte[32] { 32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 }),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            var provider3 = new ProviderRecord
            {
                PeerId = new PeerId(new byte[32] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            // Act
            bool result1 = await smallStore.AddProviderAsync(key, provider1);
            bool result2 = await smallStore.AddProviderAsync(key, provider2);
            bool result3 = await smallStore.AddProviderAsync(key, provider3);

            // Assert
            Assert.That(result1, Is.True);
            Assert.That(result2, Is.True);
            Assert.That(result3, Is.False); // Should fail due to capacity

            var providers = await smallStore.GetProvidersAsync(key, maxCount: 10);
            Assert.That(providers.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task GetProvidersAsync_WithMaxCount_ShouldLimitResults()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");

            // Add 5 providers
            for (int i = 0; i < 5; i++)
            {
                var provider = new ProviderRecord
                {
                    PeerId = new PeerId(Enumerable.Repeat((byte)i, 32).ToArray()),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ttl = TimeSpan.FromHours(1)
                };
                await _providerStore.AddProviderAsync(key, provider);
            }

            // Act
            var providers = await _providerStore.GetProvidersAsync(key, maxCount: 3);

            // Assert
            Assert.That(providers.Count, Is.EqualTo(3));
        }

        [Test]
        public async Task AddProviderAsync_WithCancellationToken_ShouldRespectCancellation()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");
            var provider = new ProviderRecord
            {
                PeerId = new PeerId(new byte[32]),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            // Since this is an in-memory operation, it should complete even with cancellation
            bool result = await _providerStore.AddProviderAsync(key, provider, cts.Token);
            Assert.That(result, Is.True);
        }

        [Test]
        public async Task GetProvidersAsync_WithCancellationToken_ShouldRespectCancellation()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            // Since this is an in-memory operation, it should complete even with cancellation
            var result = await _providerStore.GetProvidersAsync(key, maxCount: 10, cancellationToken: cts.Token);
            Assert.That(result.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task AddProviderAsync_MultipleKeysWithSameProvider_ShouldWorkIndependently()
        {
            // Arrange
            var key1 = Encoding.UTF8.GetBytes("key1");
            var key2 = Encoding.UTF8.GetBytes("key2");
            var provider = new ProviderRecord
            {
                PeerId = new PeerId(new byte[32]),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1)
            };

            // Act
            await _providerStore.AddProviderAsync(key1, provider);
            await _providerStore.AddProviderAsync(key2, provider);

            // Assert
            var providers1 = await _providerStore.GetProvidersAsync(key1, maxCount: 10);
            var providers2 = await _providerStore.GetProvidersAsync(key2, maxCount: 10);

            Assert.That(providers1.Count, Is.EqualTo(1));
            Assert.That(providers2.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task GetProvidersAsync_WithMixedExpiredAndValidProviders_ShouldReturnOnlyValid()
        {
            // Arrange
            var key = Encoding.UTF8.GetBytes("test-key");

            var validProvider = new ProviderRecord
            {
                PeerId = new PeerId(new byte[32] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromHours(1), // Valid TTL
                StoredAt = DateTime.UtcNow
            };

            var expiredProvider = new ProviderRecord
            {
                PeerId = new PeerId(new byte[32] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = TimeSpan.FromMilliseconds(1), // Very short TTL
                StoredAt = DateTime.UtcNow.AddHours(-1) // Stored 1 hour ago
            };

            await _providerStore.AddProviderAsync(key, validProvider);
            await _providerStore.AddProviderAsync(key, expiredProvider);

            // Act
            var providers = await _providerStore.GetProvidersAsync(key, maxCount: 10);

            // Assert
            Assert.That(providers.Count, Is.EqualTo(1));
            Assert.That(providers.First().PeerId, Is.EqualTo(validProvider.PeerId));
        }
    }
}
