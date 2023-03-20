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
        if (self.IsSingleSegment)
        {
            left.Append(self.First);
            return new ReadOnlySequence<byte>(left, 0, left.Next!, left.Next!.Memory.Length);
        }
        
        ReadOnlySequenceSegment<byte> right = (ReadOnlySequenceSegment<byte>)self.Start.GetObject()!;
        ReadOnlySequenceSegment<byte> startSegment = left;
        do
        {
            left = left.Append(right.Memory);
            if(right.Next is null){
                break;
            }
            right = right.Next;
        } while (true);
        return new ReadOnlySequence<byte>(startSegment, 0, left, left.Memory.Length);
    }
    
    public static ReadOnlySequence<byte> Append(this ReadOnlySequence<byte> self, ReadOnlyMemory<byte> with)
    {
        if (self.IsEmpty)
        {
            return new ReadOnlySequence<byte>(with);
        }
        MemorySegment<byte> left = new(self.First);
        if (self.IsSingleSegment)
        {
            left.Append(with);
            return new ReadOnlySequence<byte>(left, 0, left.Next!, left.Next!.Memory.Length);
        }
        
        ReadOnlySequenceSegment<byte> right = ((ReadOnlySequenceSegment<byte>)self.Start.GetObject()!).Next!;
        ReadOnlySequenceSegment<byte> startSegment = left;
        do
        {
            left = left.Append(right.Memory);
            if(right.Next is null){
                break;
            }
            right = right.Next;
        } while (true);

        left = left.Append(with);
        return new ReadOnlySequence<byte>(startSegment, 0, left, left.Memory.Length);
    }
}
