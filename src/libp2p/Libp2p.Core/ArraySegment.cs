// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Libp2p.Core;

public class MemorySegment<T> : ReadOnlySequenceSegment<T>
{
    public MemorySegment(ReadOnlyMemory<T> memory)
    {
        Memory = memory;
    }

    public MemorySegment<T> Append(ReadOnlyMemory<T> memory)
    {
        MemorySegment<T> segment = new MemorySegment<T>(memory)
        {
            RunningIndex = RunningIndex + Memory.Length
        };

        Next = segment;

        return segment;
    }
}
