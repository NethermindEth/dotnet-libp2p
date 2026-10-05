// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal sealed class BrowserWebRtcStreamChannel : IChannel
{
    private const int FlagFin = 0;
    private const int FlagReset = 2;
    private const int FlagFinAck = 3;
    private const int MaxBufferedMessages = 256;
    internal const int MaxEncodedMessageSize = 16 * 1024;

    private readonly IChannel _inner;
    private readonly Channel<byte[]> _messages = System.Threading.Channels.Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MaxBufferedMessages)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly Task _decodeTask;
    private byte[]? _currentBuffer;
    private int _currentOffset;

    public BrowserWebRtcStreamChannel(IChannel inner)
    {
        _inner = inner;
        _decodeTask = Task.Run(DecodeLoopAsync);
    }

    public TaskAwaiter GetAwaiter() => _inner.GetAwaiter();

    public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
    {
        try
        {
            if (blockingMode == ReadBlockingMode.WaitAll && length > 0)
            {
                byte[] complete = new byte[length];
                int written = 0;
                while (written < length)
                {
                    await EnsureCurrentBufferAsync(blockingMode, token);
                    byte[] current = _currentBuffer ?? throw new InvalidOperationException("RTC stream read buffer is empty.");
                    int buffered = current.Length - _currentOffset;
                    int toCopy = Math.Min(length - written, buffered);
                    current.AsSpan(_currentOffset, toCopy).CopyTo(complete.AsSpan(written));
                    _currentOffset += toCopy;
                    written += toCopy;
                }

                return new ReadResult { Result = IOResult.Ok, Data = new ReadOnlySequence<byte>(complete) };
            }

            if (_currentBuffer is null || _currentOffset >= _currentBuffer.Length)
            {
                await EnsureCurrentBufferAsync(blockingMode, token);
            }

            byte[] currentBuffer = _currentBuffer ?? throw new InvalidOperationException("RTC stream read buffer is empty.");
            int available = currentBuffer.Length - _currentOffset;
            int toRead = length == 0 || blockingMode == ReadBlockingMode.WaitAny
                ? Math.Min(length == 0 ? available : length, available)
                : Math.Min(length, available);

            if (toRead <= 0)
            {
                return ReadResult.Empty;
            }

            ReadOnlySequence<byte> data = new(new ReadOnlyMemory<byte>(currentBuffer, _currentOffset, toRead));
            _currentOffset += toRead;
            return new ReadResult { Result = IOResult.Ok, Data = data };
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return ReadResult.Ended;
        }
        catch (OperationCanceledException)
        {
            return ReadResult.Cancelled;
        }
    }

    private async Task EnsureCurrentBufferAsync(ReadBlockingMode blockingMode, CancellationToken token)
    {
        if (_currentBuffer is not null && _currentOffset < _currentBuffer.Length)
        {
            return;
        }

        if (!_messages.Reader.TryRead(out _currentBuffer!))
        {
            if (blockingMode == ReadBlockingMode.DoNotWait)
            {
                _currentBuffer = [];
                _currentOffset = 0;
                return;
            }

            _currentBuffer = await _messages.Reader.ReadAsync(token);
        }

        _currentOffset = 0;
    }

    public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        byte[] payload = bytes.ToArray();
        if (payload.Length == 0)
        {
            byte[] emptyMessage = EncodeDataMessage([]);
            return await _inner.WriteAsync(new ReadOnlySequence<byte>(emptyMessage), token).ConfigureAwait(false);
        }

        int offset = 0;
        while (offset < payload.Length)
        {
            int chunkLength = GetNextChunkLength(payload.Length - offset);
            byte[] message = EncodeDataMessage(payload.AsSpan(offset, chunkLength).ToArray());
            IOResult result = await _inner.WriteAsync(new ReadOnlySequence<byte>(message), token).ConfigureAwait(false);
            if (result != IOResult.Ok)
            {
                return result;
            }

            offset += chunkLength;
        }

        return IOResult.Ok;
    }

    public async ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
    {
        await SendFlagAsync(FlagFin, token);
        return IOResult.Ok;
    }

    public ValueTask CloseAsync() => _inner.CloseAsync();

    private async Task DecodeLoopAsync()
    {
        List<byte> pending = [];

        try
        {
            while (true)
            {
                ReadResult result = await _inner.ReadAsync(0, ReadBlockingMode.WaitAny);
                if (result.Result != IOResult.Ok)
                {
                    break;
                }

                pending.AddRange(result.Data.ToArray());
                DecodeAvailableDataChannelMessages(pending);
            }
        }
        catch (Exception ex)
        {
            _messages.Writer.TryComplete(ex);
            return;
        }

        _messages.Writer.TryComplete();
    }

    private void DecodeAvailableDataChannelMessages(List<byte> pending)
    {
        byte[] data = pending.ToArray();
        int offset = 0;
        int consumed = 0;

        while (offset < data.Length)
        {
            int lengthOffset = offset;
            if (!TryReadVarint(data, ref offset, out int messageLength))
            {
                break;
            }

            int prefixLength = offset - lengthOffset;
            if (messageLength > MaxEncodedMessageSize || prefixLength + messageLength > MaxEncodedMessageSize)
            {
                throw new FormatException($"WebRTC stream messages cannot exceed {MaxEncodedMessageSize} encoded bytes.");
            }

            if (offset + messageLength > data.Length)
            {
                offset = lengthOffset;
                break;
            }

            DecodeProtobufMessage(data.AsSpan(offset, messageLength));
            offset += messageLength;
            consumed = offset;
        }

        if (consumed > 0)
        {
            pending.RemoveRange(0, consumed);
        }
    }

    private void DecodeProtobufMessage(ReadOnlySpan<byte> message)
    {
        int offset = 0;
        while (offset < message.Length)
        {
            int key = (int)ReadVarint(message, ref offset);
            int field = key >> 3;
            int wireType = key & 0x7;

            if (field == 1 && wireType == 0)
            {
                int flag = (int)ReadVarint(message, ref offset);
                if (flag == FlagFin)
                {
                    _ = SendFlagAsync(FlagFinAck, CancellationToken.None).AsTask();
                    _messages.Writer.TryComplete();
                    return;
                }

                if (flag == FlagReset)
                {
                    _messages.Writer.TryComplete(new InvalidOperationException("Remote reset the WebRTC stream."));
                    return;
                }

                continue;
            }

            if (field == 2 && wireType == 2)
            {
                ulong length = ReadVarint(message, ref offset);
                if (length > (ulong)(message.Length - offset))
                {
                    throw new FormatException("Malformed WebRTC stream protobuf payload.");
                }

                int payloadLength = (int)length;
                if (!_messages.Writer.TryWrite(message.Slice(offset, payloadLength).ToArray()))
                {
                    throw new InvalidOperationException($"Inbound WebRTC stream buffer overflow (capacity {MaxBufferedMessages} messages).");
                }

                offset += payloadLength;
                continue;
            }

            SkipUnknownField(message, wireType, ref offset);
        }
    }

    private async ValueTask SendFlagAsync(int flag, CancellationToken token)
    {
        byte[] protobuf = EncodeFlagMessage(flag);
        await _inner.WriteAsync(new ReadOnlySequence<byte>(protobuf), token);
    }

    private static byte[] EncodeDataMessage(byte[] payload)
    {
        int protobufLength = 1 + VarInt.GetSizeInBytes(payload.Length) + payload.Length;
        int encodedLength = VarInt.GetSizeInBytes(protobufLength) + protobufLength;
        if (encodedLength > MaxEncodedMessageSize)
        {
            throw new FormatException($"WebRTC stream messages cannot exceed {MaxEncodedMessageSize} encoded bytes.");
        }

        byte[] buffer = new byte[VarInt.GetSizeInBytes(protobufLength) + protobufLength];
        int offset = 0;
        VarInt.Encode(protobufLength, buffer, ref offset);
        buffer[offset++] = 0x12;
        VarInt.Encode(payload.Length, buffer, ref offset);
        payload.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    private static int GetNextChunkLength(int remaining)
    {
        int chunkLength = Math.Min(remaining, MaxEncodedMessageSize);
        while (chunkLength > 0)
        {
            int protobufLength = 1 + VarInt.GetSizeInBytes(chunkLength) + chunkLength;
            int encodedLength = VarInt.GetSizeInBytes(protobufLength) + protobufLength;
            if (encodedLength <= MaxEncodedMessageSize)
            {
                return chunkLength;
            }

            chunkLength--;
        }

        throw new FormatException("Unable to fit WebRTC stream payload in encoded frame.");
    }

    private static byte[] EncodeFlagMessage(int flag)
    {
        int protobufLength = 2;
        byte[] buffer = new byte[VarInt.GetSizeInBytes(protobufLength) + protobufLength];
        int offset = 0;
        VarInt.Encode(protobufLength, buffer, ref offset);
        buffer[offset++] = 0x08;
        buffer[offset] = (byte)flag;
        return buffer;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> source, ref int offset)
    {
        ulong result = 0;
        int shift = 0;

        while (offset < source.Length)
        {
            byte value = source[offset++];
            result |= ((ulong)(value & 0x7f)) << shift;
            if ((value & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift >= 70)
            {
                throw new FormatException("Invalid varint.");
            }
        }

        throw new FormatException("Truncated varint.");
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> source, ref int offset, out int value)
    {
        ulong result = 0;
        int shift = 0;
        int start = offset;

        while (offset < source.Length)
        {
            byte current = source[offset++];
            result |= ((ulong)(current & 0x7f)) << shift;
            if ((current & 0x80) == 0)
            {
                if (result > int.MaxValue)
                {
                    throw new FormatException("WebRTC stream message length is too large.");
                }

                value = (int)result;
                return true;
            }

            shift += 7;
            if (shift >= 70)
            {
                throw new FormatException("Invalid varint.");
            }
        }

        offset = start;
        value = 0;
        return false;
    }

    private static void SkipUnknownField(ReadOnlySpan<byte> message, int wireType, ref int offset)
    {
        switch (wireType)
        {
            case 0:
                _ = ReadVarint(message, ref offset);
                return;
            case 2:
                ulong length = ReadVarint(message, ref offset);
                if (length > (ulong)(message.Length - offset))
                {
                    throw new FormatException("Malformed length-delimited protobuf field.");
                }

                offset += (int)length;
                return;
            default:
                throw new FormatException($"Unsupported protobuf wire type: {wireType}.");
        }
    }
}
