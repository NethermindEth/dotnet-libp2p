// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Libp2p.Core;

public interface IReader
{
    ValueTask<ReadResult> ReadAsync(int length,
        ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default);

    #region Read helpers
    async IAsyncEnumerable<ReadOnlySequence<byte>> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken token = default)
    {
        for (; ; )
        {
            token.ThrowIfCancellationRequested();

            switch (await ReadAsync(0, ReadBlockingMode.WaitAny, token: token))
            {
                case { Result: IOResult.Ok, Data: ReadOnlySequence<byte> data }: yield return data; break;
                case { Result: IOResult.Ended }: yield break;
                case { Result: IOResult.Aborted }: throw new ChannelAbortedException();
                default: throw new Exception();
            }
        }
    }

    async Task<string> ReadLineAsync()
    {
        int size = await ReadVarintAsync();
        return Encoding.UTF8.GetString((await ReadAsync(size).OrThrow()).ToArray()).TrimEnd('\n');
    }

    /// <summary>Reads a UTF-8 line with a bounded payload.</summary>
    /// <param name="maxLength">Maximum payload bytes, including a trailing newline and excluding the prefix.</param>
    /// <param name="token">Cancellation token for both prefix and payload reads.</param>
    async Task<string> ReadLineAsync(int maxLength, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        int size = await ReadVarintAsync(token);
        if (size > maxLength)
            throw new InvalidDataException($"Incoming line size {size} exceeds the limit of {maxLength} bytes");
        return Encoding.UTF8.GetString((await ReadAsync(size, token: token).OrThrow()).ToArray()).TrimEnd('\n');
    }

    Task<int> ReadVarintAsync(CancellationToken token = default)
    {
        return VarInt.Decode(this, token);
    }

    Task<ulong> ReadVarintUlongAsync()
    {
        return VarInt.DecodeUlong(this);
    }

    Task<ulong> ReadVarintUlongAsync(CancellationToken token)
    {
        return VarInt.DecodeUlong(this, token);
    }

    ValueTask<T> ReadPrefixedProtobufAsync<T>(MessageParser<T> parser, CancellationToken token = default) where T : IMessage<T>
    {
        return ReadPrefixedProtobufAsync(parser, int.MaxValue, token);
    }

    async ValueTask<T> ReadPrefixedProtobufAsync<T>(MessageParser<T> parser, int maxMessageLength, CancellationToken token) where T : IMessage<T>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessageLength);

        ulong messageLength = await ReadVarintUlongAsync(token);
        if (messageLength > (ulong)maxMessageLength)
        {
            throw new InvalidDataException($"Incoming protobuf message size {messageLength} exceeds the limit of {maxMessageLength}");
        }

        ReadOnlySequence<byte> serializedMessage = await ReadAsync((int)messageLength, token: token).OrThrow();

        return parser.ParseFrom(serializedMessage);
    }
    #endregion
}
