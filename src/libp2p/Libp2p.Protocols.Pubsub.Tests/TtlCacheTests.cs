// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class TtlCacheTests
{
    [Test]
    public void RemoveExpired_RemovesEntriesRegardlessOfKeyOrder()
    {
        using TtlCache<MessageId> cache = new(50);
        MessageId expiredHigh = new([0xFF]);
        MessageId liveLow = new([0x01]);

        cache.Add(expiredHigh);
        Thread.Sleep(80);
        cache.Add(liveLow);

        cache.RemoveExpired(DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(cache.Contains(expiredHigh), Is.False);
            Assert.That(cache.Contains(liveLow), Is.True);
        });
    }

    [Test]
    public void ExpiredEntries_AreNotReturnedBeforeTheSweeperRuns()
    {
        using TtlCache<MessageId, string> cache = new(25);
        MessageId id = new([0x01]);
        cache.Add(id, "value");

        Thread.Sleep(60);

        Assert.Multiple(() =>
        {
            Assert.That(cache.Contains(id), Is.False);
            Assert.That(cache.TryGet(id, out _), Is.False);
            Assert.That(cache.ToList(), Is.Empty);
        });
    }
}
