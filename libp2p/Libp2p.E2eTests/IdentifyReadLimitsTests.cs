// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Identify.Dto;
using NSubstitute;
using NUnit.Framework;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Libp2p.E2eTests;

public class IdentifyReadLimitsTests
{
    [TestCase(false, 8191)]
    [TestCase(true, 8191)]
    [TestCase(false, 8192)]
    [TestCase(true, 8192)]
    [TestCase(false, 8193)]
    [TestCase(true, 8193)]
    public async Task ReadAcceptsOnlyMessagesWithinEightKiB(bool push, int size)
    {
        Identity sender = TestPeers.Identity(91);
        Identify message = new() { PublicKey = sender.PublicKey.ToByteString() };
        message.AgentVersion = new string('a', size - message.CalculateSize() - 3);
        Assert.That(message.CalculateSize(), Is.EqualTo(size));
        Channel channel = new();
        Task read = ReadAsync(channel, Context(sender), push);
        Task write = channel.Reverse.WriteSizeAndProtobufAsync(message).AsTask();

        try
        {
            if (size > 8192)
            {
                Assert.ThrowsAsync<InvalidDataException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            else
            {
                await read.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            await channel.CloseAsync();
            await write;
        }
    }

    [TestCase(false, 8193ul)]
    [TestCase(true, 8193ul)]
    [TestCase(false, 1ul << 31)]
    [TestCase(true, 1ul << 31)]
    [TestCase(false, ulong.MaxValue)]
    [TestCase(true, ulong.MaxValue)]
    public async Task OversizedLengthIsRejectedBeforeReadingAnyBody(bool push, ulong length)
    {
        Channel channel = new();
        Task read = ReadAsync(channel, Context(TestPeers.Identity(91)), push);
        try
        {
            do
            {
                byte next = (byte)(length & 0x7f);
                length >>= 7;
                await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new[] { length == 0 ? next : (byte)(next | 0x80) }));
            } while (length != 0);

            Assert.ThrowsAsync<InvalidDataException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await channel.CloseAsync();
            await read.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 1)]
    [TestCase(true, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 2)]
    public async Task DeadlineCoversMissingPrefixPartialPrefixAndMissingBody(bool push, int stage)
    {
        ManualTimeProvider clock = new();
        ObservedChannel channel = new();
        Task read = ReadAsync(channel, Context(TestPeers.Identity(91)), push, clock);
        try
        {
            if (stage != 0)
            {
                byte[] prefix = stage == 1 ? [0x80] : [0x40];
                await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(prefix));
            }

            Assert.That(clock.DueTime, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(channel.ReadToken.CanBeCanceled, Is.True, "The deadline must cancel the underlying read.");
            clock.Expire();
            ChannelClosedException? error = Assert.ThrowsAsync<ChannelClosedException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.That(error!.Message, Does.Contain("Cancelled"));
        }
        finally
        {
            await channel.CloseAsync();
            await read.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        clock.Timer.Received(1).Dispose();
    }

    private static Task ReadAsync(IChannel channel, ISessionContext context, bool push, TimeProvider? clock = null) => push
        ? new IdentifyPushProtocol(new ProtocolStackSettings(), timeProvider: clock).ListenAsync(channel, context)
        : new IdentifyProtocol(new ProtocolStackSettings(), timeProvider: clock).DialAsync(channel, context);

    private static ISessionContext Context(Identity sender)
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(sender), RemotePublicKey = sender.PublicKey });
        return context;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public TimeSpan? DueTime { get; private set; }
        public ITimer Timer { get; } = Substitute.For<ITimer>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return Timer;
        }

        public void Expire() => _callback!(_state);
    }

    private sealed class ObservedChannel : IChannel
    {
        private readonly Channel _inner = new();
        public IChannel Reverse => _inner.Reverse;
        public CancellationToken ReadToken { get; private set; }
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            ReadToken = token;
            return _inner.ReadAsync(length, blockingMode, token);
        }
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => _inner.WriteAsync(bytes, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => _inner.WriteEofAsync(token);
        public ValueTask CloseAsync() => _inner.CloseAsync();
        public ValueTask AbortAsync() => _inner.AbortAsync();
        public TaskAwaiter GetAwaiter() => _inner.GetAwaiter();
    }
}
