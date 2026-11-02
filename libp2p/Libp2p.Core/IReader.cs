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
    /// <summary>
    /// Reads bytes with explicit ownership. Dispose the result after its data is no longer used.
    /// Implementations without pooled storage fall back to ordinary reads.
    /// </summary>
    ValueTask<ReadResult> ReadOwnedAsync(int length,
        ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default) => ReadAsync(length, blockingMode, token);

    ValueTask<ReadResult> ReadAsync(int length,
        ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default);

    #region Read helpers
    /// <summary>Reads scoped chunks. Each result is disposed when enumeration advances or ends.</summary>
    /// <remarks>Retain <see cref="ReadResult.ToSlice"/> to forward pooled bytes beyond an iteration.</remarks>
    async IAsyncEnumerable<ReadResult> ReadAllOwnedAsync(
        [EnumeratorCancellation] CancellationToken token = default)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using ReadResult result = await ReadOwnedAsync(0, ReadBlockingMode.WaitAny, token).ConfigureAwait(false);
            if (result.Result == IOResult.Ended) yield break;
            if (result.Result == IOResult.Aborted) throw new ChannelAbortedException();
            if (result.Result != IOResult.Ok) throw new ChannelClosedException(result.Result);
            yield return result;
        }
    }

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

    /// <summary>Reads a UTF-8 line with a maximum payload of 1,024 bytes, including any trailing newline.</summary>
    /// <exception cref="InvalidDataException">The declared payload exceeds 1,024 bytes.</exception>
    /// <exception cref="FormatException">The length prefix exceeds <see cref="int.MaxValue"/> or five bytes.</exception>
    Task<string> ReadLineAsync() => ReadLineAsync(1024);

    /// <summary>Reads a UTF-8 line with a bounded payload.</summary>
    /// <param name="maxLength">Maximum payload length in bytes, including any trailing newline, excluding the length prefix.</param>
    /// <param name="token">Cancellation token for both prefix and payload reads.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is negative.</exception>
    /// <exception cref="InvalidDataException">The declared payload exceeds <paramref name="maxLength"/>.</exception>
    /// <exception cref="FormatException">The length prefix exceeds <see cref="int.MaxValue"/> or five bytes.</exception>
    async Task<string> ReadLineAsync(int maxLength, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

        int size = await ReadVarintAsync(token);
        if (size > maxLength)
        {
            throw new InvalidDataException($"Incoming line size {size} exceeds the limit of {maxLength} bytes");
        }

        using ReadResult read = await ReadOwnedAsync(size, token: token);
        return Encoding.UTF8.GetString(read.OrThrow()).TrimEnd('\n');
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

        using ReadResult read = await ReadOwnedAsync((int)messageLength, token: token);
        return parser.ParseFrom(read.OrThrow());
    }
    #endregion
}
