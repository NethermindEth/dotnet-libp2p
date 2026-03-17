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
    private readonly RTCDataChannel _dataChannel;
    private readonly System.Threading.Channels.Channel<byte[]> _incoming = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _currentBuffer;
    private int _currentOffset;

    public DataChannelOverIChannel(RTCDataChannel dataChannel)
    {
        _dataChannel = dataChannel;

        _dataChannel.onmessage += OnMessage;
        _dataChannel.onclose += () => Complete();
        _dataChannel.onerror += _ => Complete();
    }

    public TaskAwaiter GetAwaiter() => _completion.Task.GetAwaiter();

    public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
    {
        try
        {
            if (_currentBuffer is null || _currentOffset >= _currentBuffer.Length)
            {
                if (!_incoming.Reader.TryRead(out _currentBuffer!))
                {
                    if (blockingMode == ReadBlockingMode.DontWait)
                    {
                        return ReadResult.Empty;
                    }

                    _currentBuffer = await _incoming.Reader.ReadAsync(token);
                }

                _currentOffset = 0;
            }

            if (_currentBuffer is null)
            {
                return ReadResult.Ended;
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

        byte[] payload = bytes.ToArray();
        _dataChannel.send(payload);
        return ValueTask.FromResult(IOResult.Ok);
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

    private void OnMessage(RTCDataChannel dc, DataChannelPayloadProtocols protocol, byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        _incoming.Writer.TryWrite(data);
    }

    private void Complete()
    {
        _incoming.Writer.TryComplete();
        _completion.TrySetResult();
    }
}