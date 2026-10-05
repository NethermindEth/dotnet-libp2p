// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using SIPSorcery.Net;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal sealed class SctpWebRtcDataChannel : IChannel
{
    private const int MaxBufferedMessages = 256;

    private readonly RTCPeerSctpAssociation _association;
    private readonly System.Threading.Channels.Channel<byte[]> _incoming = System.Threading.Channels.Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MaxBufferedMessages)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _currentBuffer;
    private int _currentOffset;
    private int _closed;

    public SctpWebRtcDataChannel(RTCPeerSctpAssociation association, ushort streamId)
    {
        _association = association;
        StreamId = streamId;
    }

    public ushort StreamId { get; }

    public Task Opened => _opened.Task;

    public TaskAwaiter GetAwaiter() => _completion.Task.GetAwaiter();

    public void MarkOpen() => _opened.TrySetResult();

    public void Receive(SctpDataFrame frame)
    {
        if (_completion.Task.IsCompleted)
        {
            return;
        }

        if (frame.UserData.Length == 0 &&
            frame.PPID is (uint)DataChannelPayloadProtocols.WebRTC_Binary_Empty or
                (uint)DataChannelPayloadProtocols.WebRTC_String_Empty)
        {
            return;
        }

        if (!_incoming.Writer.TryWrite(frame.UserData))
        {
            Complete(new InvalidOperationException($"Inbound SCTP data channel buffer overflow (capacity {MaxBufferedMessages} messages)."));
        }
    }

    public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
    {
        try
        {
            if (_currentBuffer is null || _currentOffset >= _currentBuffer.Length)
            {
                if (!_incoming.Reader.TryRead(out _currentBuffer!))
                {
                    if (blockingMode == ReadBlockingMode.DoNotWait)
                    {
                        return ReadResult.Empty;
                    }

                    _currentBuffer = await _incoming.Reader.ReadAsync(token);
                }

                _currentOffset = 0;
            }

            int available = _currentBuffer.Length - _currentOffset;
            int toRead = length == 0 || blockingMode == ReadBlockingMode.WaitAny
                ? Math.Min(length == 0 ? available : length, available)
                : Math.Min(length, available);

            if (toRead <= 0)
            {
                return ReadResult.Empty;
            }

            ReadOnlySequence<byte> result = new(new ReadOnlyMemory<byte>(_currentBuffer, _currentOffset, toRead));
            _currentOffset += toRead;
            return new ReadResult { Result = IOResult.Ok, Data = result };
        }
        catch (ChannelClosedException)
        {
            return ReadResult.Ended;
        }
        catch (OperationCanceledException)
        {
            return ReadResult.Cancelled;
        }
    }

    public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        if (_completion.Task.IsCompleted)
        {
            return ValueTask.FromResult(IOResult.Ended);
        }

        if (token.IsCancellationRequested)
        {
            return ValueTask.FromResult(IOResult.Cancelled);
        }

        try
        {
            byte[] payload = bytes.ToArray();
            _association.SendData(StreamId, (uint)DataChannelPayloadProtocols.WebRTC_Binary, payload);
            return ValueTask.FromResult(IOResult.Ok);
        }
        catch (Exception ex)
        {
            Complete(new InvalidOperationException("Failed to send data on SCTP data channel.", ex));
            return ValueTask.FromResult(IOResult.InternalError);
        }
    }

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
    {
        Complete();
        return ValueTask.FromResult(IOResult.Ok);
    }

    public ValueTask CloseAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }

    private void Complete(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        _incoming.Writer.TryComplete(error);
        _opened.TrySetCanceled();
        if (error is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(error);
        }
    }
}
