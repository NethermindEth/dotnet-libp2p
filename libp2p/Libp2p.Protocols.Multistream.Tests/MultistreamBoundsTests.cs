// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Multistream.Tests;

[Parallelizable(ParallelScope.All)]
public class MultistreamBoundsTests
{
    public enum NegotiationRead
    {
        DialHello,
        ListenHello,
        DialReply,
        ListenProposal,
        OptimisticHello,
        OptimisticReply
    }

    [Test]
    public async Task MalformedMessageAbortsNegotiation(
        [Values] NegotiationRead stage, [Values] bool overflow, [Values] bool coalesced)
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;
        IConnectionContext context = Substitute.For<IConnectionContext>();
        IProtocol selected = Substitute.For<IProtocol>();
        selected.Id.Returns("/test/bounds");
        context.SubProtocols.Returns([selected]);
        bool optimistic = stage is NegotiationRead.OptimisticHello or NegotiationRead.OptimisticReply;
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.UpgradeOptions.Returns(new UpgradeOptions
        {
            SelectedProtocol = optimistic ? selected : null,
            CompletionSource = completion
        });
        RecordingLogger logger = new();
        using ILoggerFactory factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        MultistreamProtocol protocol = new(factory);
        bool listen = stage is NegotiationRead.ListenHello or NegotiationRead.ListenProposal;
        Task negotiation = listen ? protocol.ListenAsync(channel, context) : protocol.DialAsync(channel, context);
        Task<IOResult>? write = null;
        try
        {
            Assert.That(await remote.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(protocol.Id));
            if (optimistic)
                Assert.That(await remote.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(selected.Id));

            if (stage is NegotiationRead.DialReply or NegotiationRead.ListenProposal or NegotiationRead.OptimisticReply)
                await remote.WriteLineAsync(protocol.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            if (stage == NegotiationRead.DialReply)
                Assert.That(await remote.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(selected.Id));

            byte[] prefix = overflow ? [0xff, 0xff, 0xff, 0xff, 0x7f] : [0x81, 0x08];
            // This body cannot be consumed by the rejecting reader. Abort must release its writer.
            byte[] frame = coalesced ? [.. prefix, 0x61, 0x62, 0x63, 0x0a] : prefix;
            write = remote.WriteAsync(new ReadOnlySequence<byte>(frame)).AsTask();
            await negotiation.WaitAsync(TimeSpan.FromSeconds(2));
            IOResult writeResult = await write.WaitAsync(TimeSpan.FromSeconds(2));

            if (coalesced) Assert.That(writeResult, Is.AnyOf(IOResult.Aborted, IOResult.Ended));
            Assert.That(channel.GetAwaiter().IsCompleted, Is.True);
            Assert.That((await remote.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
            Assert.That((await channel.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
            await context.DidNotReceiveWithAnyArgs().Upgrade(default!, default(IProtocol)!);
            Assert.That(logger.Levels, Has.None.GreaterThanOrEqualTo(LogLevel.Warning));
            Assert.That(logger.Errors, Has.Some.InstanceOf(overflow ? typeof(FormatException) : typeof(InvalidDataException)));
            if (optimistic)
            {
                Assert.That(completion.Task.IsFaulted, Is.True);
                Assert.That(completion.Task.Exception!.InnerException, Is.TypeOf<ChannelClosedException>());
            }
        }
        finally
        {
            await channel.AbortAsync();
            await Task.WhenAll(negotiation, write ?? Task.CompletedTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public enum NegotiationMode { Dial, Listen, OptimisticDial }

    [Test]
    public async Task ValidMessageAtPayloadLimitReachesUpgrade(
        [Values] NegotiationMode mode, [Values] bool upgradeFails)
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;
        IConnectionContext context = Substitute.For<IConnectionContext>();
        IProtocol selected = Substitute.For<IProtocol>();
        // 1,023 UTF-8 bytes plus the newline is exactly the 1,024-byte payload cap.
        selected.Id.Returns("/" + new string('a', 1022));
        context.SubProtocols.Returns([selected]);
        context.UpgradeOptions.Returns(new UpgradeOptions
        {
            SelectedProtocol = mode == NegotiationMode.OptimisticDial ? selected : null
        });
        FormatException? upgradeError = upgradeFails ? new FormatException("Application upgrade failed") : null;
        context.Upgrade(channel, selected).Returns(upgradeError is null ? Task.CompletedTask : Task.FromException(upgradeError));
        MultistreamProtocol protocol = new();
        Task negotiation = mode == NegotiationMode.Listen
            ? protocol.ListenAsync(channel, context) : protocol.DialAsync(channel, context);
        Task peer = Exchange();
        try
        {
            Task exchange = Task.WhenAll(negotiation, peer);
            if (upgradeError is null)
                await exchange.WaitAsync(TimeSpan.FromSeconds(2));
            else
                Assert.That(Assert.ThrowsAsync<FormatException>(async () => await exchange.WaitAsync(TimeSpan.FromSeconds(2))),
                    Is.SameAs(upgradeError));
            await context.Received(1).Upgrade(channel, selected);
        }
        finally
        {
            await channel.AbortAsync();
            await Task.WhenAll(negotiation, peer).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        async Task Exchange()
        {
            Assert.That(await remote.ReadLineAsync(), Is.EqualTo(protocol.Id));
            if (mode == NegotiationMode.OptimisticDial)
                Assert.That(await remote.ReadLineAsync(), Is.EqualTo(selected.Id));
            await remote.WriteLineAsync(protocol.Id);
            if (mode == NegotiationMode.Dial)
                Assert.That(await remote.ReadLineAsync(), Is.EqualTo(selected.Id));
            await remote.WriteLineAsync(selected.Id);
            if (mode == NegotiationMode.Listen)
                Assert.That(await remote.ReadLineAsync(), Is.EqualTo(selected.Id));
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];
        public List<Exception?> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Errors.Add(exception);
        }
    }
}
