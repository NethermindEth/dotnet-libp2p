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

    public TaskAwaiter GetAwaiter()
    {
        return _channel.GetAwaiter();
    }

    public IChannel Reverse()
    {
        return _channel.Reverse;
    }

    // Adapt to IReader.ReadAsync(int length, ReadBlockingMode, CancellationToken)
    public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        => _channel.ReadAsync(length, blockingMode, token);

    public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        return _channel.WriteAsync(bytes, token);
    }

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
    {
        return _channel.WriteEofAsync(token);
    }

    public ValueTask CloseAsync()
    {
        return _channel.CloseAsync();
    }
}
