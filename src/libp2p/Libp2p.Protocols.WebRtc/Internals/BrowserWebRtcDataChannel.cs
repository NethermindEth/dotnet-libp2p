// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.JSInterop;
using Nethermind.Libp2p.Core;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal sealed class BrowserWebRtcDataChannel : IChannel, IAsyncDisposable
{
    private const int MaxBufferedMessages = 256;

    private readonly IJSRuntime _jsRuntime;
    private readonly DotNetObjectReference<BrowserWebRtcDataChannel> _dotNetRef;
    private readonly Channel<byte[]> _incoming = System.Threading.Channels.Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MaxBufferedMessages)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _currentBuffer;
    private int _currentOffset;
    private int _closed;
    private string? _channelId;

    public BrowserWebRtcDataChannel(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
        _dotNetRef = DotNetObjectReference.Create(this);
    }

    public DotNetObjectReference<BrowserWebRtcDataChannel> DotNetRef => _dotNetRef;
    public Task Opened => _opened.Task;
    public Task Completed => _completion.Task;
    public string ChannelId => _channelId ?? throw new InvalidOperationException("RTC data channel has not been initialized.");

    public void SetChannelId(string channelId)
    {
        _channelId = channelId;
    }

    [JSInvokable]
    public Task OpenedFromJs()
    {
        _opened.TrySetResult();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task ReceiveMessage(byte[] data)
    {
        if (data.Length == 0 || _completion.Task.IsCompleted)
        {
            return Task.CompletedTask;
        }

        if (!_incoming.Writer.TryWrite(data))
        {
            Complete(new InvalidOperationException($"Inbound RTC data channel buffer overflow (capacity {MaxBufferedMessages} messages)."));
        }

        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task ClosedFromJs(string? reason)
    {
        Complete(string.IsNullOrWhiteSpace(reason) ? null : new InvalidOperationException(reason));
        return Task.CompletedTask;
    }

    public TaskAwaiter GetAwaiter() => _completion.Task.GetAwaiter();

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
                    byte[] current = _currentBuffer ?? throw new InvalidOperationException("RTC data channel read buffer is empty.");
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

            byte[] currentBuffer = _currentBuffer ?? throw new InvalidOperationException("RTC data channel read buffer is empty.");
            int available = currentBuffer.Length - _currentOffset;
            int toRead = length == 0 || blockingMode == ReadBlockingMode.WaitAny
                ? Math.Min(length == 0 ? available : length, available)
                : Math.Min(length, available);

            if (toRead <= 0)
            {
                return ReadResult.Empty;
            }

            ReadOnlySequence<byte> result = new(new ReadOnlyMemory<byte>(currentBuffer, _currentOffset, toRead));
            _currentOffset += toRead;
            return new ReadResult { Result = IOResult.Ok, Data = result };
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

        if (!_incoming.Reader.TryRead(out _currentBuffer!))
        {
            if (blockingMode == ReadBlockingMode.DoNotWait)
            {
                _currentBuffer = [];
                _currentOffset = 0;
                return;
            }

            _currentBuffer = await _incoming.Reader.ReadAsync(token);
        }

        _currentOffset = 0;
    }

    public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        if (_completion.Task.IsCompleted)
        {
            return IOResult.Ended;
        }

        if (token.IsCancellationRequested)
        {
            return IOResult.Cancelled;
        }

        if (_channelId is null)
        {
            throw new InvalidOperationException("RTC data channel has not been initialized.");
        }

        try
        {
            await _jsRuntime.InvokeVoidAsync("nethermindLibp2pWebRtcDirect.send", token, _channelId, bytes.ToArray());
            return IOResult.Ok;
        }
        catch (JSException ex)
        {
            Complete(new InvalidOperationException("Failed to send data on browser RTC data channel.", ex));
            return IOResult.InternalError;
        }
    }

    public async ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
    {
        await CloseAsync();
        return IOResult.Ok;
    }

    public async ValueTask CloseAsync()
    {
        if (_channelId is not null)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("nethermindLibp2pWebRtcDirect.closeChannel", _channelId);
            }
            catch (JSException)
            {
            }
        }

        Complete();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _dotNetRef.Dispose();
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
