// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class TtlCacheTests
{
    [Test]
    public void RemoveExpired_RemovesEntriesRegardlessOfKeyOrder()
    {
        using TtlCache<MessageId> cache = new(500);
        MessageId expiredHigh = new([0xFF]);
        MessageId liveLow = new([0x01]);

        cache.Add(expiredHigh);
        Thread.Sleep(750);
        cache.Add(liveLow);

        cache.RemoveExpired(DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(cache.Contains(expiredHigh), Is.False);
            Assert.That(cache.Contains(liveLow), Is.True);
        });
    }

    [Test]
    public void ExpiredEntries_AreNotReturned()
    {
        using TtlCache<MessageId, string> cache = new(500);
        MessageId id = new([0x01]);
        cache.Add(id, "value");

        Thread.Sleep(750);

        Assert.Multiple(() =>
        {
            Assert.That(cache.Contains(id), Is.False);
            Assert.That(cache.TryGet(id, out _), Is.False);
            Assert.That(cache.ToList(), Is.Empty);
        });
    }

    [Test]
    public void Add_ReplacesAnExpiredEntry()
    {
        using TtlCache<MessageId, string> cache = new(500);
        MessageId id = new([0x01]);
        cache.Add(id, "expired");

        Thread.Sleep(750);
        cache.Add(id, "replacement");

        Assert.That(cache.Get(id), Is.EqualTo("replacement"));
    }

    [Test]
    public void Add_EvictsTheOldestLiveEntryAtCapacity()
    {
        using TtlCache<MessageId, string> cache = new(ttl: 1_000, maxEntries: 2);
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
}
