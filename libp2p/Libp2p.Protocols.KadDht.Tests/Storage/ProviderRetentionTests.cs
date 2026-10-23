// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht.Storage;
using Nethermind.Libp2p.Core;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Storage;

public class ProviderRetentionTests
{
    [Test]
    public async Task DefaultStore_TwentyThousandKeys_StopsAtTenThousandWithoutMaintenance()
    {
        var store = new InMemoryProviderStore();
        var record = Record();
        int admitted = 0;
        for (int i = 0; i < 20_000; i++)
            if (await store.AddProviderAsync(BitConverter.GetBytes(i), record)) admitted++;

        Assert.That(admitted, Is.EqualTo(10_000));
        Assert.That(store.KeyCount, Is.EqualTo(10_000));
        Assert.That(store.TotalProviderCount, Is.EqualTo(10_000));
    }

    [Test]
    public async Task DefaultStore_DoesNotRetainOversizedKey()
    {
        var store = new InMemoryProviderStore();
        Assert.That(await store.AddProviderAsync(new byte[1_000_000], Record()), Is.False);
        Assert.That(store.KeyCount, Is.Zero);
    }

    [Test]
    public async Task Capacity_PreservesLiveRecordsAndAllowsNewerRefreshes()
    {
        var clock = new TestClock();
        var store = CreateStore(clock, keys: 1, providers: 1);
        byte[] key = [1];
        var first = Record(clock);
        Assert.That(await store.AddProviderAsync(key, first), Is.True);
        Assert.That(await store.AddProviderAsync(new byte[] { 2 }, first), Is.False);
        Assert.That(await store.AddProviderAsync(key, Record(clock, peer: 2)), Is.False);
        var refresh = first with { Timestamp = 2, Multiaddrs = new[] { "updated" } };
        Assert.That(await store.AddProviderAsync(key, refresh), Is.True);
        Assert.That(await store.AddProviderAsync(key, first), Is.True);
        Assert.That(await store.AddProviderAsync(key, refresh with { Multiaddrs = new[] { "equal" } }), Is.True);
        var stored = (await store.GetProvidersAsync(key)).Single();
        Assert.That(stored.Timestamp, Is.EqualTo(2));
        Assert.That(stored.Multiaddrs, Is.EqualTo(new[] { "updated" }));
        Assert.That(store.KeyCount, Is.EqualTo(1));
        Assert.That(store.ExpiryIndexCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Capacity_ReclaimsUnrelatedExpiredBucketWithoutReadsOrMaintenance()
    {
        var clock = new TestClock();
        var store = CreateStore(clock, keys: 2);
        var record = Record(clock);
        await store.AddProviderAsync(new byte[] { 1 }, record);
        await store.AddProviderAsync(new byte[] { 2 }, record);
        clock.Advance(TimeSpan.FromHours(1));
        // Expiration is strictly greater than the deadline.
        Assert.That(await store.AddProviderAsync(new byte[] { 3 }, Record(clock)), Is.False);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.That(await store.AddProviderAsync(new byte[] { 3 }, Record(clock)), Is.True);
        Assert.That(store.KeyCount, Is.EqualTo(2)); // Only the one bucket needed is reclaimed.
        Assert.That(store.ExpiryIndexCount, Is.EqualTo(2));
        Assert.That((await store.GetProvidersAsync(new byte[] { 3 })).Single().PeerId, Is.EqualTo(record.PeerId));
    }

    [Test]
    public async Task MixedExpiry_UsesLatestDeadlineAndPrunesRequestedKeyBeforeProviderLimit()
    {
        var clock = new TestClock();
        var store = CreateStore(clock, keys: 2, providers: 2);
        var shortLived = Record(clock);
        var longLived = Record(clock, peer: 2) with { Ttl = TimeSpan.FromHours(2) };
        await store.AddProviderAsync(new byte[] { 1 }, shortLived);
        await store.AddProviderAsync(new byte[] { 1 }, longLived);
        await store.AddProviderAsync(new byte[] { 2 }, shortLived);
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
        Assert.That(await store.AddProviderAsync(new byte[] { 3 }, Record(clock)), Is.True);
        Assert.That(await store.AddProviderAsync(new byte[] { 1 }, Record(clock, peer: 3)), Is.True);
        Assert.That((await store.GetProvidersAsync(new byte[] { 1 })).Select(p => p.PeerId), Does.Contain(longLived.PeerId));
        Assert.That((await store.GetProvidersAsync(new byte[] { 1 })).Count, Is.EqualTo(2));
        Assert.That(await store.HasProvidersAsync(new byte[] { 2 }), Is.False);
        Assert.That(store.KeyCount, Is.EqualTo(2));
        Assert.That(store.ExpiryIndexCount, Is.EqualTo(2));
    }

    [Test]
    public async Task ExpiryIndex_RefreshAndEveryRemovalPathKeepOneEntryPerKey()
    {
        var clock = new TestClock();
        var store = CreateStore(clock, keys: 5);
        for (int i = 0; i < 100; i++)
        {
            await store.AddProviderAsync(new byte[] { 1 }, Record(clock) with { Timestamp = i, Ttl = TimeSpan.FromHours(i + 1) });
            Assert.That(store.ExpiryIndexCount, Is.EqualTo(1));
        }
        await store.AddProviderAsync(new byte[] { 1 }, Record(clock, peer: 2));
        Assert.That(await store.RemoveProviderAsync(new byte[] { 1 }, Record(clock).PeerId), Is.True);
        Assert.That(store.ExpiryIndexCount, Is.EqualTo(1));
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
        Assert.That(await store.HasProvidersAsync(new byte[] { 1 }), Is.False); // deadline updated after removing latest provider
        Assert.That(store.ExpiryIndexCount, Is.Zero);

        await store.AddProviderAsync(new byte[] { 2 }, Record(clock));
        Assert.That(await store.RemoveAllProvidersAsync(new byte[] { 2 }), Is.EqualTo(1));
        Assert.That(store.ExpiryIndexCount, Is.Zero);
        await store.AddProviderAsync(new byte[] { 3 }, Record(clock));
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
        Assert.That(await store.GetProvidersAsync(new byte[] { 3 }), Is.Empty);
        Assert.That(store.ExpiryIndexCount, Is.Zero);
        await store.AddProviderAsync(new byte[] { 4 }, Record(clock));
        await store.AddProviderAsync(new byte[] { 4 }, Record(clock, peer: 2) with { Ttl = TimeSpan.FromHours(2) });
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
        Assert.That(await store.CleanupExpiredProvidersAsync(), Is.EqualTo(1));
        Assert.That(store.ExpiryIndexCount, Is.EqualTo(1));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.That(await store.CleanupExpiredProvidersAsync(), Is.EqualTo(1));
        Assert.That(store.ExpiryIndexCount, Is.Zero);
        Assert.That(store.GetAllKeys(), Is.Empty);
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    [TestCase(false, true)]
    public async Task RefreshAndReclamation_AreAtomicInBothOrderings(bool refreshFirst, bool cleanup)
    {
        var clock = new TestClock();
        var store = CreateStore(clock, keys: 1);
        var original = Record(clock);
        await store.AddProviderAsync(new byte[] { 1 }, original);
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
        var refreshed = Record(clock) with { Timestamp = 2 };
        Func<Task<bool>> refresh = () => store.AddProviderAsync(new byte[] { 1 }, refreshed);
        var other = Record(clock);
        Func<Task<bool>> reclaim = cleanup
            ? async () => await store.CleanupExpiredProvidersAsync() > 0
            : () => store.AddProviderAsync(new byte[] { 2 }, other);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        clock.OnNextRead = () =>
        {
            entered.Set(); // The first operation holds the store lock here.
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        };
        var first = Task.Run(refreshFirst ? refresh : reclaim);
        Task<bool>? second = null;
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second = Task.Run(async () =>
            {
                started.SetResult();
                return await (refreshFirst ? reclaim : refresh)();
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
        }
        bool firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
        bool secondResult = await second!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(firstResult, Is.True);
        Assert.That(secondResult, Is.EqualTo(cleanup && !refreshFirst));
        if (refreshFirst || cleanup)
            Assert.That((await store.GetProvidersAsync(new byte[] { 1 })).Single().Timestamp, Is.EqualTo(2));
        else
        {
            Assert.That(await store.GetProvidersAsync(new byte[] { 1 }), Is.Empty);
            Assert.That(await store.HasProvidersAsync(new byte[] { 2 }), Is.True);
        }
        Assert.That(store.ExpiryIndexCount, Is.EqualTo(store.KeyCount));
    }

    [Test]
    public async Task GatedParallelAdmissions_RespectBothAggregateLimits()
    {
        var clock = new TestClock();
        var store = CreateStore(clock, keys: 3, providers: 2);
        var ready = new CountdownEvent(100);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 100).Select(async i =>
        {
            ready.Signal();
            await release.Task;
            return await Task.Run(() => store.AddProviderAsync(BitConverter.GetBytes(i % 10), Record(clock, peer: i)));
        }).ToArray();
        Assert.That(ready.IsSet, Is.True);
        release.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(results.Count(r => r), Is.EqualTo(6));
        Assert.That(store.KeyCount, Is.EqualTo(3));
        Assert.That(store.TotalProviderCount, Is.EqualTo(6));
        foreach (var key in store.GetAllKeys())
            Assert.That((await store.GetProvidersAsync(key)).Count, Is.EqualTo(2));
        Assert.That(store.ExpiryIndexCount, Is.EqualTo(3));
        ready.Dispose();
    }

    [Test]
    public async Task Payload_ExactIdentityBoundaryAndLargePeerRejection()
    {
        var record = Record();
        // Four raw key bytes => eight Base64 chars; zero bytes encode as 32 Base58 '1' chars.
        int identityBytes = 16 + 32 + 64;
        var exact = new InMemoryProviderStore(1, 1, 1, identityBytes);
        Assert.That(await exact.AddProviderAsync(new byte[4], record), Is.True);
        Assert.That(await new InMemoryProviderStore(1, 1, 1, identityBytes - 1).AddProviderAsync(new byte[4], record), Is.False);
        Assert.That(await exact.AddProviderAsync(new byte[4], record with { PeerId = new PeerId(new byte[1_000_000]) }), Is.False);
        // A nonzero ID needs more Base58 characters than the raw precheck accounts for.
        var nonzero = record with { PeerId = new PeerId(Enumerable.Repeat((byte)255, 32).ToArray()) };
        Assert.That(await exact.AddProviderAsync(new byte[4], nonzero), Is.False);
    }

    [Test]
    public async Task Payload_OmitsWholeAddressesAndOwnsReadOnlySnapshot()
    {
        var record = Record();
        var callerAddresses = new List<string> { new('x', 1000), "é😀", "tail", "ignored" };
        var store = new InMemoryProviderStore(1, 1, 3, 8 + 32 + 64 + 6);
        Assert.That(await store.AddProviderAsync(new byte[] { 1 }, record with { Multiaddrs = callerAddresses }), Is.True);
        callerAddresses.Clear();
        var stored = (await store.GetProvidersAsync(new byte[] { 1 })).Single();
        Assert.That(stored.Multiaddrs, Is.EqualTo(new[] { "é😀" })); // three UTF-16 chars, exactly six bytes
        Assert.That(stored.Multiaddrs, Is.AssignableTo<IList<string>>());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)stored.Multiaddrs).Add("changed"));
        Assert.That((await store.GetProvidersAsync(new byte[] { 1 })).Single().Multiaddrs, Is.EqualTo(new[] { "é😀" }));
    }

    [Test]
    public async Task Payload_AddressCountIncludesSkippedSlots()
    {
        var store = new InMemoryProviderStore(1, 1, 2, 1024);
        await store.AddProviderAsync(new byte[] { 1 }, Record() with { Multiaddrs = new[] { new string('x', 1024), "kept", "third" } });
        Assert.That((await store.GetProvidersAsync(new byte[] { 1 })).Single().Multiaddrs, Is.EqualTo(new[] { "kept" }));
    }

    [Test]
    public async Task InvalidExpiration_DoesNotPublishPartialChanges()
    {
        var clock = new TestClock();
        var store = CreateStore(clock, keys: 1);
        var record = Record(clock);
        await store.AddProviderAsync(new byte[] { 1 }, record);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AddProviderAsync(new byte[] { 1 }, record with
        {
            Timestamp = 2,
            StoredAt = DateTime.MaxValue,
            Ttl = TimeSpan.FromHours(1)
        }));
        Assert.That((await store.GetProvidersAsync(new byte[] { 1 })).Single().Timestamp, Is.EqualTo(1));
        Assert.That(store.ExpiryIndexCount, Is.EqualTo(1));
    }

    [TestCase(0, 1, 1, 1)]
    [TestCase(1, 0, 1, 1)]
    [TestCase(1, 1, 0, 1)]
    [TestCase(1, 1, 1, 0)]
    [TestCase(-1, 1, 1, 1)]
    [TestCase(1, -1, 1, 1)]
    [TestCase(1, 1, -1, 1)]
    [TestCase(1, 1, 1, -1)]
    public void Constructor_RejectsNonpositiveLimits(int providers, int keys, int addresses, int bytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryProviderStore(providers, keys, addresses, bytes));

    [Test]
    public async Task LegacyConstructorAndCancellationBehavior_ArePreserved()
    {
        var store = new InMemoryProviderStore(1, null);
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        Assert.That(await store.AddProviderAsync(new byte[] { 1 }, Record(), stop.Token), Is.True);
        Assert.That(await store.HasProvidersAsync(new byte[] { 1 }, stop.Token), Is.True);
        Assert.That(await store.GetProvidersAsync(new byte[] { 1 }, cancellationToken: stop.Token), Has.Count.EqualTo(1));
        Assert.ThrowsAsync<TaskCanceledException>(() => store.CleanupExpiredProvidersAsync(stop.Token));
        Assert.That(await store.RemoveProviderAsync(new byte[] { 1 }, Record().PeerId, stop.Token), Is.True);
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryProviderStore(0));
    }

    private static InMemoryProviderStore CreateStore(TestClock clock, int keys, int providers = 2) =>
        new(providers, keys, 16, 1024, clock);

    private static ProviderRecord Record(TestClock? clock = null, int peer = 0) => new()
    {
        PeerId = new PeerId(Enumerable.Repeat((byte)peer, 32).ToArray()),
        Timestamp = 1,
        StoredAt = clock?.GetUtcNow().UtcDateTime ?? DateTime.UtcNow,
        Ttl = TimeSpan.FromHours(1)
    };

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        internal Action? OnNextRead;
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref OnNextRead, null)?.Invoke();
            return _now;
        }
        internal void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
