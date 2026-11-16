// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.Ping.Tests;

[TestFixture]
public class PingProtocolDialTests
{
    [Test]
    public async Task DialAsync_SendsDistinctPayloads_WhenDialsRunConcurrently()
    {
        const int Workers = 8;
        const int DialsPerWorker = 2_500;
        PingProtocol protocol = new();
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = "/ip4/127.0.0.1/tcp/1234" });
        ConcurrentDictionary<string, byte> payloads = new();
        int duplicates = 0;
        using Barrier start = new(Workers);

        Task[] workers = Enumerable.Range(0, Workers).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < DialsPerWorker; i++)
            {
                EchoChannel channel = new();
                protocol.DialAsync(channel, context).GetAwaiter().GetResult();
                if (!payloads.TryAdd(Convert.ToHexString(channel.Payload), 0))
                {
                    Interlocked.Increment(ref duplicates);
                }
            }
        }, TaskCreationOptions.LongRunning)).ToArray();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.That(duplicates, Is.Zero);
    }

    private sealed class EchoChannel : IChannel
    {
        public byte[] Payload { get; private set; } = [];

        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            Payload = bytes.ToArray();
            return ValueTask.FromResult(IOResult.Ok);
        }

        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
            => ValueTask.FromResult(new ReadResult { Result = IOResult.Ok, Data = new ReadOnlySequence<byte>(Payload) });

        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => ValueTask.FromResult(IOResult.Ok);
        public ValueTask CloseAsync() => ValueTask.CompletedTask;
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public TaskAwaiter GetAwaiter() => Task.CompletedTask.GetAwaiter();
    }
}
