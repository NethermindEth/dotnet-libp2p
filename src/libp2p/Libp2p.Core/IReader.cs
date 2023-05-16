// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Nethermind.Libp2p.Core;

public interface IReader
{
    async Task<string> ReadLineAsync()
    {
        int size = await ReadVarintAsync();
        return Encoding.UTF8.GetString((await ReadAsync(size)).ToArray()).TrimEnd('\n');
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

    async IAsyncEnumerable<ReadOnlySequence<byte>> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken token = default)
    {
        while (!token.IsCancellationRequested)
        {
            yield return await ReadAsync(0, ReadBlockingMode.WaitAny, token);
        }
    }
}

public enum ReadBlockingMode
{
    WaitAll,
    WaitAny,
    DontWait
}
