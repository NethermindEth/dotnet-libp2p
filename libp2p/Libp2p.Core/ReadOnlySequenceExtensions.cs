// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Libp2p.Core;

public static class ReadOnlySequenceExtensions
{
    public static ReadOnlySequence<byte> Prepend(this ReadOnlySequence<byte> self, ReadOnlyMemory<byte> with)
    {
        if (self.IsEmpty)
        {
            return new ReadOnlySequence<byte>(with);
        }

        MemorySegment<byte> left = new(with);
        ReadOnlySequenceSegment<byte> startSegment = left;
        foreach (ReadOnlyMemory<byte> segment in self)
        {
            left = left.Append(segment);
        }

        return new ReadOnlySequence<byte>(startSegment, 0, left, left.Memory.Length);
    }

    public static ReadOnlySequence<byte> Append(this ReadOnlySequence<byte> self, ReadOnlyMemory<byte> with)
    {
        if (self.IsEmpty)
        {
            return new ReadOnlySequence<byte>(with);
        }

        ReadOnlySequence<byte>.Enumerator enumerator = self.GetEnumerator();
        enumerator.MoveNext();
        MemorySegment<byte> left = new(enumerator.Current);
        ReadOnlySequenceSegment<byte> startSegment = left;
        while (enumerator.MoveNext())
        {
            left = left.Append(enumerator.Current);
        }

        left = left.Append(with);
        return new ReadOnlySequence<byte>(startSegment, 0, left, left.Memory.Length);
    }
}
