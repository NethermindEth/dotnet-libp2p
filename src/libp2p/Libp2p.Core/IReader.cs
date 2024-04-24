// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Nethermind.Libp2p.Core;

public interface IReader
{
    ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default);


    #region Read helpers
    async IAsyncEnumerable<ReadOnlySequence<byte>> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken token = default)
    {
        for (; ; )
        {
            token.ThrowIfCancellationRequested();

            switch (await ReadAsync(0, ReadBlockingMode.WaitAny, token))
            {
                case { Result: IOResult.Ok, Data: ReadOnlySequence<byte> data }: yield return data; break;
                case { Result: IOResult.Ended }: yield break;
                default: throw new Exception();
            }
        }
    }

    async Task<string> ReadLineAsync()
    {
        int size = await ReadVarintAsync();
        return Encoding.UTF8.GetString((await ReadAsync(size).OrThrow()).ToArray()).TrimEnd('\n');
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
        ReadOnlySequence<byte> serializedMessage = await ReadAsync(messageLength, token: token).OrThrow();

        return parser.ParseFrom(serializedMessage);
    }
    #endregion
}
