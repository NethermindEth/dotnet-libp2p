// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class TtlCacheTests
{
    [Test]
    public void RemoveExpired_RemovesEntriesRegardlessOfKeyOrder()
    {
        TestTimeProvider clock = new();
        using TtlCache<MessageId> cache = new(500, clock);
        MessageId expiredHigh = new([0xFF]);
        MessageId liveLow = new([0x01]);

        cache.Add(expiredHigh);
        clock.UtcNow = clock.UtcNow.AddMilliseconds(500);
        cache.Add(liveLow);

        cache.RemoveExpired(clock.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(cache.Count, Is.EqualTo(1));
            Assert.That(cache.Contains(expiredHigh), Is.False);
            Assert.That(cache.Contains(liveLow), Is.True);
        });
    }

    [TestCase("Contains")]
    [TestCase("TryGet")]
    [TestCase("Get")]
    [TestCase("ToList")]
    public void ExpiredEntries_AreNotReturnedBeforeSweeping(string operation)
    {
        TestTimeProvider clock = new();
        using TtlCache<MessageId, string> cache = new(500, clock);
        MessageId id = new([0x01]);
        cache.Add(id, "value");
        clock.UtcNow = clock.UtcNow.AddMilliseconds(500);

        // No cache read or sweep may remove the expired entry before the operation under test.
        Assert.That(cache.Count, Is.EqualTo(1));
        switch (operation)
        {
            case "Contains":
                Assert.That(cache.Contains(id), Is.False);
                break;
            case "TryGet":
                Assert.That(cache.TryGet(id, out string value), Is.False);
                Assert.That(value, Is.Null);
                break;
            case "Get":
                Assert.That(cache.Get(id), Is.Null);
                break;
            case "ToList":
                Assert.That(cache.ToList(), Is.Empty);
                break;
        }
    }

    [Test]
    public void ToList_ReturnsOnlyLiveEntries()
    {
        TestTimeProvider clock = new();
        using TtlCache<MessageId, string> cache = new(500, clock);
        cache.Add(new([0x01]), "expired");
        clock.UtcNow = clock.UtcNow.AddMilliseconds(500);
        cache.Add(new([0x02]), "live");

        Assert.That(cache.Count, Is.EqualTo(2));
        Assert.That(cache.ToList(), Is.EqualTo(new[] { "live" }));
    }

    [Test]
    public void Add_ReplacesAnExpiredEntry()
    {
        TestTimeProvider clock = new();
        using TtlCache<MessageId, string> cache = new(500, clock);
        MessageId id = new([0x01]);
        cache.Add(id, "expired");
        clock.UtcNow = clock.UtcNow.AddMilliseconds(500);

        Assert.That(cache.Count, Is.EqualTo(1));
        cache.Add(id, "replacement");

        Assert.That(cache.Count, Is.EqualTo(1));
        Assert.That(cache.Get(id), Is.EqualTo("replacement"));
    }

    [Test]
    public void Add_DoesNotReplaceOrRefreshALiveEntry()
    {
        TestTimeProvider clock = new();
        using TtlCache<MessageId, string> cache = new(500, clock);
        MessageId id = new([0x01]);
        cache.Add(id, "original");
        clock.UtcNow = clock.UtcNow.AddMilliseconds(250);
        cache.Add(id, "replacement");

        Assert.That(cache.Get(id), Is.EqualTo("original"));
        clock.UtcNow = clock.UtcNow.AddMilliseconds(250);
        Assert.That(cache.Contains(id), Is.False);
    }

    [Test]
    public void Add_EvictsTheOldestLiveEntryAtCapacity()
    {
        TestTimeProvider clock = new();
        using TtlCache<MessageId, string> cache = new(ttl: 500, maxEntries: 2, timeProvider: clock);
        MessageId first = new([0x01]);
        MessageId second = new([0x02]);
        MessageId third = new([0x03]);

        cache.Add(first, "first");
        cache.Add(second, "second");
        cache.Add(third, "third");

        Assert.Multiple(() =>
        {
            Assert.That(cache.Contains(first), Is.False);
            Assert.That(cache.Get(second), Is.EqualTo("second"));
            Assert.That(cache.Get(third), Is.EqualTo("third"));
        });
    }

    [Test]
    public void RemoveExpired_RemovesLaterEntriesAfterClockMovesBackward()
    {
        TestTimeProvider clock = new();
        using TtlCache<MessageId> cache = new(500, clock);
        MessageId first = new([0x01]);
        MessageId second = new([0x02]);
        cache.Add(first);
        clock.UtcNow = clock.UtcNow.AddMilliseconds(-250);
        cache.Add(second);
        clock.UtcNow = clock.UtcNow.AddMilliseconds(500);

        cache.RemoveExpired(clock.UtcNow);

        Assert.That(cache.Count, Is.EqualTo(1));
        Assert.That(cache.Contains(first), Is.True);
        Assert.That(cache.Contains(second), Is.False);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;

        // Keep the background sweeper dormant so each test controls expiry explicitly.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Substitute.For<ITimer>();
    }
}
