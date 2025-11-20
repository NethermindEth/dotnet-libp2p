// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT
// SPDX-Author: Luca Fabbri

using System.Buffers;

namespace Nethermind.Libp2p.Core.Extensions;

/// <summary>
/// A <see cref="Stream"/> implementation that uses an <see cref="IChannel"/> for reading and writing.
/// </summary>
internal sealed class ChannelStream : Stream
{
    private readonly IChannel _channel;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelStream"/> class.
    /// </summary>
    /// <param name="channel">The input channel</param>
    public ChannelStream(IChannel channel)
    {
        _channel = channel;
    }

    /// <summary>
    /// Indicates whether the stream can be read from.
    /// </summary>
    public override bool CanRead => true;

    /// <summary>
    /// Indicates whether the stream supports seeking.
    /// </summary>
    public override bool CanSeek => false;

    /// <summary>
    /// Indicates whether the stream supports writing.
    /// </summary>
    public override bool CanWrite => true;

    private long _length = 0;

    /// <summary>
    /// Length is not supported for Channel Streams, it always returns 0.
    /// </summary>
    public override long Length => _length;

    /// <summary>
    /// Postion in the stream is not supported for Channel Streams.
    /// </summary>
    public override long Position { get; set; }

    /// <summary>
    /// Flushes the stream is not supported for Channel Streams
    /// </summary>
    /// <exception cref="NotSupportedException">Always throws</exception>
    public override void Flush()
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        ReadResult result = await _channel.ReadAsync(count, ReadBlockingMode.DontWait, cancellationToken);

        if (result.Result != IOResult.Ok)
        {
            return 0; // No more data to read
        }

        int bytesRead = (int)result.Data.Length;
        result.Data.ToArray().CopyTo(new Memory<byte>(buffer, offset, bytesRead));
        return bytesRead;
    }

    /// <summary>
    /// Seeks to a specific position in the stream is not supported for Channel Streams
    /// </summary>
    /// <param name="offset">The offset</param>
    /// <param name="origin">The seek origin</param>
    /// <returns>New origin</returns>
    /// <exception cref="NotSupportedException">Always throw</exception>
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// Sets the length of the stream is not supported for Channel Streams
    /// </summary>
    /// <param name="value">The value to set</param>
    /// <exception cref="NotSupportedException">Always throws</exception>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        ReadOnlySequence<byte> sequence = new(buffer, offset, count);
        await _channel.WriteAsync(sequence, cancellationToken).ConfigureAwait(false);
    }
}
