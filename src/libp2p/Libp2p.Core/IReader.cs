// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using System.Runtime.CompilerServices;
using System.Text;

namespace Nethermind.Libp2p.Core;

public interface IReader
{
    ValueTask<ReadResult> ReadAsync(int length,
        ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default);

    #region Read helpers
    async IAsyncEnumerable<PooledBuffer.Slice> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken token = default)
    {
        for (; ; )
        {
            token.ThrowIfCancellationRequested();

            ReadResult result = await ReadAsync(0, ReadBlockingMode.WaitAny, token: token);
            switch (result)
            {
                case { Result: IOResult.Ok, Length: > 0 }:
                    PooledBuffer.Slice slice = result.ToSlice();
                    result.Dispose();
                    yield return slice;
                    break;
                case { Result: IOResult.Ok }:
                    result.Dispose();
                    break;
                case { Result: IOResult.Ended }:
                    result.Dispose();
                    yield break;
                default:
                    result.Dispose();
                    throw new Exception();
            }
        }
    }

    async Task<string> ReadLineAsync()
    {
        int size = await ReadVarintAsync();
        using ReadResult result = await ReadAsync(size).OrThrow();
        return Encoding.UTF8.GetString(result.Data).TrimEnd('\n');
    }

    Task<int> ReadVarintAsync(CancellationToken token = default)
    {
        return VarInt.Decode(this, token);
    }

    Task<ulong> ReadVarintUlongAsync()
    {
        return VarInt.DecodeUlong(this);
    }

    async ValueTask<T> ReadPrefixedProtobufAsync<T>(MessageParser<T> parser, CancellationToken token = default) where T : IMessage<T>
    {
        int messageLength = await ReadVarintAsync(token);
        using ReadResult serializedMessage = await ReadAsync(messageLength, token: token).OrThrow();
        return parser.ParseFrom(serializedMessage.Data);
    }
    #endregion
}
