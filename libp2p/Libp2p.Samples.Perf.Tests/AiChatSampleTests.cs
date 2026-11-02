// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Libp2p.Core;
using NUnit.Framework;

namespace Nethermind.Libp2p.Samples.Perf.Tests;

public class AiChatSampleTests
{
    [TestCase(1101)]
    [TestCase(1024 * 1024)]
    public void AiChatAcceptsLongPromptLengthBeforeReadingPayload(int payloadLength)
    {
        IChannel channel = new PrefixOnlyChannel(payloadLength);
        // Stop at the payload read, before any request to the external Ollama service.
        PayloadReadException? exception = Assert.ThrowsAsync<PayloadReadException>(async () =>
            await new ChatProtocol().DialAsync(channel, null!));
        Assert.That(exception!.Length, Is.EqualTo(payloadLength));
    }

    [Test]
    public void AiChatRejectsPromptAboveApplicationLimitBeforeReadingPayload()
    {
        IChannel channel = new PrefixOnlyChannel(1024 * 1024 + 1);

        Assert.ThrowsAsync<InvalidDataException>(async () => await new ChatProtocol().DialAsync(channel, null!));
    }

    private sealed class PrefixOnlyChannel : IChannel
    {
        private readonly byte[] _prefix;
        private int _consumed;

        public PrefixOnlyChannel(int length)
        {
            _prefix = new byte[VarInt.GetSizeInBytes(length)];
            int offset = 0;
            VarInt.Encode(length, _prefix, ref offset);
        }

        public ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            if (_consumed == _prefix.Length) throw new PayloadReadException(length);
            Assert.That(length, Is.EqualTo(1));
            return ValueTask.FromResult(new ReadResult
            {
                Result = IOResult.Ok,
                Data = new ReadOnlySequence<byte>(_prefix.AsMemory(_consumed++, 1))
            });
        }

        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
            => throw new NotSupportedException();
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask CloseAsync() => ValueTask.CompletedTask;
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public TaskAwaiter GetAwaiter() => Task.CompletedTask.GetAwaiter();
    }

    private sealed class PayloadReadException(int length) : Exception
    {
        public int Length { get; } = length;
    }
}
