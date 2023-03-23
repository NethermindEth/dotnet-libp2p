// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Libp2p.Core.Tests;

public class ReadOnlySequenceExtensionsTests
{
    [Test]
    public void Test_SequenceIsPrepended()
    {
        ReadOnlySequence<byte> sequence = new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 });
        ReadOnlySequence<byte> prepended = sequence.Prepend(new byte[] { 0 });
        Assert.That(sequence.Length, Is.EqualTo(3));
        Assert.That(prepended.Length, Is.EqualTo(4));
        Assert.That(prepended.ToArray(), Is.EquivalentTo(new byte[] { 0, 1, 2, 3 }));

        ReadOnlySequence<byte> prepended2 = prepended.Prepend(new byte[] { 42 });
        Assert.That(sequence.Length, Is.EqualTo(3));
        Assert.That(prepended.Length, Is.EqualTo(4));
        Assert.That(prepended2.Length, Is.EqualTo(5));
        Assert.That(prepended2.ToArray(), Is.EquivalentTo(new byte[] { 42, 0, 1, 2, 3 }));
        Assert.That(prepended2.ToArray()[0], Is.EqualTo(42));
    }

    [Test]
    public void Test_SequenceIsAppended()
    {
        ReadOnlySequence<byte> sequence = new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 });
        ReadOnlySequence<byte> prepended = sequence.Append(new byte[] { 0 });
        Assert.That(sequence.Length, Is.EqualTo(3));
        Assert.That(prepended.Length, Is.EqualTo(4));
        Assert.That(prepended.ToArray(), Is.EquivalentTo(new byte[] { 1, 2, 3, 0 }));

        ReadOnlySequence<byte> prepended2 = prepended.Append(new byte[] { 42 });
        Assert.That(sequence.Length, Is.EqualTo(3));
        Assert.That(prepended.Length, Is.EqualTo(4));
        Assert.That(prepended2.Length, Is.EqualTo(5));
        Assert.That(prepended2.ToArray(), Is.EquivalentTo(new byte[] { 1, 2, 3, 0, 42 }));
    }
}
