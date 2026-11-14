// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Tests;

public class MessageIdTests
{
    [Test]
    public void EqualBytesInDifferentArraysAreEqualWithEqualHashCodes()
    {
        MessageId first = new([1, 2, 3]);
        MessageId second = new([1, 2, 3]);

        Assert.That(first, Is.EqualTo(second));
        Assert.That(first == second, Is.True);
        Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
        Assert.That(first, Is.Not.EqualTo(new MessageId([1, 2, 4])));
    }

    [Test]
    public void NullBytesMatchTheDefaultId()
    {
        MessageId nullBytes = new(null!);

        Assert.That(nullBytes, Is.EqualTo(default(MessageId)));
        Assert.That(nullBytes.GetHashCode(), Is.EqualTo(default(MessageId).GetHashCode()));
    }

    [Test]
    public void ComparerFindsAnIdByItsBytes()
    {
        Dictionary<MessageId, int> ids = new(MessageIdComparer.Instance) { [new MessageId([1, 2, 3])] = 7 };
        Dictionary<MessageId, int>.AlternateLookup<ReadOnlySpan<byte>> lookup = ids.GetAlternateLookup<ReadOnlySpan<byte>>();

        Assert.That(lookup.TryGetValue([1, 2, 3], out int value), Is.True);
        Assert.That(value, Is.EqualTo(7));
        Assert.That(lookup.ContainsKey([1, 2, 4]), Is.False);
        Assert.That(MessageIdComparer.Instance.GetHashCode([1, 2, 3]), Is.EqualTo(new MessageId([1, 2, 3]).GetHashCode()));
    }

    [Test]
    public void KnownFixedSeedCollisionDoesNotCollide()
    {
        // "costarring" and "liquid" have the same unseeded 32-bit FNV-1a hash. Remote peers choose
        // message IDs, so a fixed hash lets them fill one hash bucket of the router's caches.
        MessageId first = new("costarring"u8.ToArray());
        MessageId second = new("liquid"u8.ToArray());

        Assert.That(first.GetHashCode(), Is.Not.EqualTo(second.GetHashCode()));
    }
}
