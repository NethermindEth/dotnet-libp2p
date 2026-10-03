// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using SIPSorcery.Net;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal class DataChannelOverIChannel : IChannel
{
    private const int MaxBufferedMessages = 256;

    private readonly RTCDataChannel _dataChannel;
    private readonly System.Threading.Channels.Channel<byte[]> _incoming = System.Threading.Channels.Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MaxBufferedMessages)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _currentBuffer;
    private int _currentOffset;
    private int _closed;
    private int _aborted;

    public DataChannelOverIChannel(RTCDataChannel dataChannel)
    {
        _dataChannel = dataChannel;

        _dataChannel.onmessage += OnMessage;
        _dataChannel.onclose += () => Complete();
        _dataChannel.onerror += error => Complete(new InvalidOperationException($"RTC data channel error: {error}"));
    }

    public TaskAwaiter GetAwaiter() => _completion.Task.GetAwaiter();

    public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
    {
        if (Volatile.Read(ref _aborted) != 0)
            return ReadResult.Aborted;

        try
        {
            if (_currentBuffer is null || _currentOffset >= _currentBuffer.Length)
            {
                if (!_incoming.Reader.TryRead(out _currentBuffer!))
                {
                    if (blockingMode == ReadBlockingMode.DoNotWait)
                    {
                        return Volatile.Read(ref _aborted) != 0 ? ReadResult.Aborted : ReadResult.Empty;
                    }

                    _currentBuffer = await _incoming.Reader.ReadAsync(token);
                }

                _currentOffset = 0;
            }

            if (_currentBuffer is null)
            {
                return Volatile.Read(ref _aborted) != 0 ? ReadResult.Aborted : ReadResult.Ended;
            }

            int available = _currentBuffer.Length - _currentOffset;
            int toRead = length == 0
                ? available
                : (blockingMode == ReadBlockingMode.WaitAny ? Math.Min(length, available) : Math.Min(length, available));

            if (toRead <= 0)
            {
                return ReadResult.Empty;
            }

            ReadOnlySequence<byte> result = new(new ReadOnlyMemory<byte>(_currentBuffer, _currentOffset, toRead));
            _currentOffset += toRead;
            return Volatile.Read(ref _aborted) != 0
                ? ReadResult.Aborted
                : new ReadResult { Result = IOResult.Ok, Data = result };
        }
        catch (ChannelClosedException)
        {
            return Volatile.Read(ref _aborted) != 0 ? ReadResult.Aborted : ReadResult.Ended;
        }
        catch (OperationCanceledException)
        {
            return Volatile.Read(ref _aborted) != 0 ? ReadResult.Aborted : ReadResult.Cancelled;
        }
    }

    public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        if (Volatile.Read(ref _aborted) != 0)
            return ValueTask.FromResult(IOResult.Aborted);

        if (_completion.Task.IsCompleted)
        {
            return ValueTask.FromResult(IOResult.Ended);
        }

        if (token.IsCancellationRequested)
        {
            return ValueTask.FromResult(IOResult.Cancelled);
        }

        byte[] payload = bytes.ToArray();
        try
        {
            _dataChannel.send(payload);
            return ValueTask.FromResult(IOResult.Ok);
        }
        catch (Exception ex)
        {
            Complete(new InvalidOperationException("Failed to send data on RTC data channel.", ex));
            return ValueTask.FromResult(IOResult.InternalError);
        }
    }

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
    {
        if (Volatile.Read(ref _aborted) != 0)
            return ValueTask.FromResult(IOResult.Aborted);

        Complete();
        return ValueTask.FromResult(IOResult.Ok);
    }

    public ValueTask CloseAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }

    public ValueTask AbortAsync()
    {
        Interlocked.Exchange(ref _aborted, 1);
        Complete();
        return ValueTask.CompletedTask;
    }

    private void OnMessage(RTCDataChannel dc, DataChannelPayloadProtocols protocol, byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (!_incoming.Writer.TryWrite(data))
        {
            Complete(new InvalidOperationException($"Inbound RTC data channel buffer overflow (capacity {MaxBufferedMessages} messages)."));
        }
    }

    private void Complete(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        _incoming.Writer.TryComplete(error);
        if (error is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(error);
        }

        try
        {
            _dataChannel.close();
        }
        catch
        {
        }
    }
}
