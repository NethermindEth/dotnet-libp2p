// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core.TestsBase;

public class TestChannel : IChannel
{
    private readonly Channel _channel;

    public TestChannel()
    {
        _channel = new Channel();
    }

    public bool IsClosed => _channel.IsClosed;
    public CancellationToken Token => _channel.Token;

    public Task CloseAsync(bool graceful = true)
    {
        return _channel.CloseAsync();
    }

    public void OnClose(Func<Task> action)
    {
        _channel.OnClose(action);
    }

    public TaskAwaiter GetAwaiter()
    {
        return _channel.GetAwaiter();
    }

    public IChannel Reverse()
    {
        return _channel.Reverse;
    }

    public ValueTask<ReadOnlySequence<byte>> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default)
    {
        return _channel.ReadAsync(length, blockingMode, token);
    }

    public ValueTask WriteAsync(ReadOnlySequence<byte> bytes)
    {
        return _channel.WriteAsync(bytes);
    }
}
