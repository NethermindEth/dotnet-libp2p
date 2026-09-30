// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

// TODO: Add tests
[TestFixture]
public class YamuxProtocolTests
{
    [Test]
    public async Task ResetCancelsPumpWaitingForRemoteWindow()
    {
        IProtocol protocol = Substitute.For<IProtocol>();
        protocol.Id.Returns("/test/1.0.0");

        IConnectionContext context = Substitute.For<IConnectionContext>();
        INewSessionContext session = Substitute.For<INewSessionContext>();
        context.UpgradeToSession().Returns(session);
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        session.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        session.Id.Returns("dialer");
        session.DialRequests.Returns([new UpgradeOptions { SelectedProtocol = protocol }]);
        session.SubProtocols.Returns([protocol]);
        TestChannel appChannel = new();
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(appChannel);

        StreamClosedLogger logger = new();
        ILoggerFactory loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        TestChannel transport = new();
        Task yamux = new YamuxProtocol(loggerFactory: loggerFactory).DialAsync(transport, context);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remote = transport.Reverse();
            Task<IOResult> upload = appChannel.Reverse().WriteAsync(new ReadOnlySequence<byte>(new byte[512 * 1024]), timeout.Token).AsTask();

            int received = 0;
            int streamId = 0;
            while (received < YamuxProtocol.ProtocolInitialWindowSize)
            {
                YamuxHeader header = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
                if (header.StreamID != 0)
                    streamId = header.StreamID;
                if (header.Type == YamuxHeaderType.Data && header.Length > 0)
                {
                    await remote.ReadAsync(header.Length, token: timeout.Token).OrThrow();
                    received += header.Length;
                }
            }

            Assert.That(await upload, Is.EqualTo(IOResult.Ok));
            Assert.That(streamId, Is.EqualTo(1));
            byte[] reset = new byte[12];
            YamuxHeader resetHeader = new() { Type = YamuxHeaderType.WindowUpdate, Flags = YamuxHeaderFlags.Rst, StreamID = streamId };
            YamuxHeader.ToBytes(reset, ref resetHeader);
            await remote.WriteAsync(new ReadOnlySequence<byte>(reset), timeout.Token);
            await logger.Closed.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task HalfClosedStreamFlushesResponseBeyondInitialWindow()
    {
        IProtocol protocol = Substitute.For<IProtocol>();
        protocol.Id.Returns("/test/1.0.0");

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        INewSessionContext dialerSession = Substitute.For<INewSessionContext>();
        dialerContext.UpgradeToSession().Returns(dialerSession);
        dialerContext.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        dialerSession.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        dialerSession.Id.Returns("dialer");
        dialerSession.DialRequests.Returns([new UpgradeOptions { SelectedProtocol = protocol }]);
        dialerSession.SubProtocols.Returns([protocol]);
        TestChannel dialerAppChannel = new();
        dialerSession.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerAppChannel);

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        INewSessionContext listenerSession = Substitute.For<INewSessionContext>();
        listenerContext.UpgradeToSession().Returns(listenerSession);
        listenerContext.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        listenerSession.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        listenerSession.Id.Returns("listener");
        listenerSession.SubProtocols.Returns([protocol]);
        TestChannel listenerAppChannel = new();
        listenerSession.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerAppChannel);

        TestChannel transport = new();
        YamuxProtocol yamux = new();
        Task listen = yamux.ListenAsync(transport.Reverse(), listenerContext);
        Task dial = yamux.DialAsync(transport, dialerContext);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel dialerApp = dialerAppChannel.Reverse();
            IChannel listenerApp = listenerAppChannel.Reverse();
            await dialerApp.WriteEofAsync(timeout.Token);
            Assert.That((await listenerApp.ReadAsync(0, ReadBlockingMode.WaitAny, timeout.Token)).Result, Is.EqualTo(IOResult.Ended));

            byte[] response = new byte[1024 * 1024];
            new Random(12345).NextBytes(response);
            await listenerApp.WriteAsync(new ReadOnlySequence<byte>(response), timeout.Token);
            await listenerApp.WriteEofAsync(timeout.Token);
            await listenerApp.CloseAsync();

            using MemoryStream received = new();
            await foreach (ReadOnlySequence<byte> chunk in dialerApp.ReadAllAsync(timeout.Token))
            {
                foreach (ReadOnlyMemory<byte> segment in chunk)
                    received.Write(segment.Span);
            }

            Assert.That(received.ToArray(), Is.EqualTo(response));
        }
        finally
        {
            await transport.CloseAsync();
            await Task.WhenAll(listen, dial).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    // TODO:
    // Implement the following test cases:
    // Establish connection, expect 0 stream
    // Close connection, expect goaway
    // Try speak a protocol
    // Exchange data
    // Expect error and react to it

    [Test]
    public async Task Test_Protocol_Communication2()
    {
        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        INewSessionContext dialerSessionContext = Substitute.For<INewSessionContext>();
        dialerContext.UpgradeToSession().Returns(dialerSessionContext);
        dialerContext.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        dialerSessionContext.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        dialerSessionContext.Id.Returns("dialer");

        dialerSessionContext.DialRequests.Returns([new UpgradeOptions() { SelectedProtocol = proto1 }]);

        TestChannel dialerDownChannel = new();
        dialerSessionContext.SubProtocols.Returns([proto1]);
        TestChannel dialerUpChannel = new();
        dialerSessionContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        _ = dialerUpChannel.Reverse().WriteLineAsync("hello").AsTask().ContinueWith((e) => dialerUpChannel.CloseAsync());

        IChannel listenerDownChannel = dialerDownChannel.Reverse();

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        INewSessionContext listenerSessionContext = Substitute.For<INewSessionContext>();
        listenerContext.UpgradeToSession().Returns(listenerSessionContext);
        listenerContext.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        listenerSessionContext.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        listenerSessionContext.Id.Returns("listener");

        listenerSessionContext.SubProtocols.Returns([proto1]);
        TestChannel listenerUpChannel = new();
        listenerSessionContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        YamuxProtocol proto = new(loggerFactory: new TestContextLoggerFactory());

        _ = proto.ListenAsync(listenerDownChannel, listenerContext);

        _ = proto.DialAsync(dialerDownChannel, dialerContext);


        string res = await listenerUpChannel.Reverse().ReadLineAsync();
        await listenerUpChannel.CloseAsync();

        Assert.That(res, Is.EqualTo("hello"));
    }

    private sealed class StreamClosedLogger : ILogger
    {
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("stream 1: Closed", StringComparison.Ordinal))
                Closed.TrySetResult();
        }
    }
}
