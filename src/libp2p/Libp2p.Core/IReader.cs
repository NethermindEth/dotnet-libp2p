// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text;

namespace Nethermind.Libp2p.Core;

public interface IReader
{
    async Task<string> ReadLineAsync(bool prependedWithSize = true)
    {
        int size = (int)(await ReadVarintAsync());
        return Encoding.UTF8.GetString(await ReadAsync(size)).TrimEnd('\n');
    }

    Task<int> ReadVarintAsync()
    {
        return VarInt.Decode(this);
    }

    Task<ulong> ReadVarintUlongAsync()
    {
        return VarInt.DecodeUlong(this);
    }

    ValueTask<ReadOnlySequence<byte>> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default);
}

public enum ReadBlockingMode
{
    WaitAll,
    WaitAny,
    DontWait
}


// class X
// {
//     void P()
//     {
//         System.Threading.Channels.Channel<byte> c = System.Threading.Channels.Channel.CreateBounded<byte>(0);
//     }
// }