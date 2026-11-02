// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using NSubstitute;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class RequestResponseTests
{
    [Test]
    public async Task RegisteredReadUsesFinalServiceClock(
        [Values(false, true)] bool cancellationHandler,
        [Values(false, true)] bool dialer,
        [Values(false, true)] bool explicitClock)
    {
        TimeSpan readTimeout = TimeSpan.FromSeconds(10);
        ManualTimeProvider serviceClock = new(), suppliedClock = new();
        using var builder = new Libp2pPeerFactoryBuilder();
        int handlerCalls = 0;
        Task<StringValue> Handle(StringValue request)
        {
            handlerCalls++;
            return Task.FromResult(request);
        }
        var result = cancellationHandler
            ? builder.AddRequestResponseProtocol<StringValue, StringValue>(readTimeout,
                (request, _, _) => Handle(request), "/test/registered-clock", isExposed: false,
                timeProvider: explicitClock ? suppliedClock : null)
            : builder.AddRequestResponseProtocol<StringValue, StringValue>(readTimeout,
                "/test/registered-clock", (request, _) => Handle(request), isExposed: false,
                timeProvider: explicitClock ? suppliedClock : null);
        // Services added after protocol registration must be visible when Build creates the protocol.
        builder.Services.AddSingleton<TimeProvider>(serviceClock);
        builder.Build();
        var registration = builder.ServiceProvider.GetRequiredService<IProtocolStackSettings>().Protocols!.Keys
            .Single(p => p.Id == "/test/registered-clock");
        var protocol = (RequestResponseProtocol<StringValue, StringValue>)registration.Protocol;
        Assert.That(result, Is.SameAs(builder));
        Assert.That(registration.IsExposed, Is.False);
        Assert.That(protocol.ReadTimeout, Is.EqualTo(readTimeout));

        var context = Substitute.For<ISessionContext>();
        context.State.Returns(new State());
        Channel wire = new();
        ObservedChannel channel = new(wire);
        Task exchange = dialer
            ? protocol.DialAsync(channel, context, new StringValue())
            : protocol.ListenAsync(channel, context);
        ManualTimeProvider clock = explicitClock ? suppliedClock : serviceClock;
        try
        {
            if (dialer)
                await wire.Reverse.ReadPrefixedProtobufAsync(StringValue.Parser).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await channel.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(clock.CreatedTimers, Is.EqualTo(1), "The registered protocol must use the selected clock.");
            Assert.That((explicitClock ? serviceClock : suppliedClock).CreatedTimers, Is.Zero);
            Assert.That(clock.DueTimes, Is.EqualTo(new[] { readTimeout }));
            Assert.That(exchange.IsCompleted, Is.False);
            clock.Advance(readTimeout - TimeSpan.FromTicks(1));
            Assert.That(exchange.IsCompleted, Is.False);
            clock.Advance(TimeSpan.FromTicks(1));
            Assert.ThrowsAsync<ChannelClosedException>(async () => await exchange.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.That(handlerCalls, Is.Zero);
            Assert.That(clock.DisposedTimers, Is.EqualTo(1));
        }
        finally
        {
            await wire.AbortAsync();
            await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task Test_RequestResponse()
    {
        E2eTestSetup test = new();
        int request = 1;

        await test.AddPeersAsync(2);
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);
        int response = await session.DialAsync<IncrementNumberTestProtocol, int, int>(1);

        Assert.That(response, Is.EqualTo(request + 1));
    }

    private sealed class ObservedChannel(IChannel inner) : IChannel
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken CancellationToken => inner.CancellationToken;
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            var read = inner.ReadAsync(length, blockingMode, token);
            ReadStarted.TrySetResult();
            return read;
        }
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => inner.WriteAsync(bytes, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
        public ValueTask CloseAsync() => inner.CloseAsync();
        public ValueTask AbortAsync() => inner.AbortAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
    }
}
