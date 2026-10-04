// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Yamux;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

// TODO: Add tests
[TestFixture]
public class YamuxProtocolTests
{
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(60 * 24 * 60 * 60)]
    public void ClosedStreamIdleTimeoutMustFitTimerRange(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YamuxProtocol(
            closedStreamIdleTimeout: TimeSpan.FromSeconds(seconds)));
    }

    [Test]
    public void ClosedStreamIdleTimeoutAcceptsMaximumTimerDuration()
    {
        Assert.DoesNotThrow(() => new YamuxProtocol(
            closedStreamIdleTimeout: TimeSpan.FromMilliseconds(uint.MaxValue - 1)));
    }

    [Test]
    public void ClosedStreamIdleTimeoutRejectsAboveMaximumTimerDuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YamuxProtocol(
            closedStreamIdleTimeout: TimeSpan.FromMilliseconds(uint.MaxValue)));
    }

    [Test]
    public void ReceiveWindowCannotBeSmallerThanProtocolDefault()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YamuxProtocol(
            windowSettings: new YamuxWindowSettings
            {
                InitialWindowSize = YamuxProtocol.ProtocolInitialWindowSize - 1
            }));
    }

    [Test]
    public async Task AbortedOutboundChannelSendsResetInsteadOfFin()
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
        TaskCompletionSource upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            upgraded.TrySetResult();
            return appChannel;
        });

        TestChannel transport = new();
        Task yamux = new YamuxProtocol().DialAsync(transport, context);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            await upgraded.Task.WaitAsync(timeout.Token);
            Task<IOResult> pendingWrite = appChannel.Reverse().WriteAsync(
                new ReadOnlySequence<byte>(new byte[] { 1 }), timeout.Token).AsTask();
            await appChannel.CloseAsync();
            Assert.That(await pendingWrite.WaitAsync(timeout.Token), Is.EqualTo(IOResult.Ended));

            IChannel remote = transport.Reverse();
            YamuxHeader syn = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            YamuxHeader reset = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());

            Assert.That(syn.Flags, Is.EqualTo(YamuxHeaderFlags.Syn));
            Assert.That(reset.Flags, Is.EqualTo(YamuxHeaderFlags.Rst));
            Assert.That(reset.StreamID, Is.EqualTo(syn.StreamID));
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task DiscardedInboundDataSendsResetInsteadOfFin()
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

        ObservedInboundChannel appChannel = new();
        TaskCompletionSource upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            upgraded.TrySetResult();
            return appChannel;
        });

        TestChannel transport = new();
        Task yamux = new YamuxProtocol().DialAsync(transport, context);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            await upgraded.Task.WaitAsync(timeout.Token);
            IChannel remote = transport.Reverse();
            YamuxHeader syn = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            Assert.That(syn.Flags, Is.EqualTo(YamuxHeaderFlags.Syn));

            byte[] frame = new byte[13];
            YamuxHeader data = new() { Type = YamuxHeaderType.Data, Length = 1, StreamID = syn.StreamID };
            YamuxHeader.ToBytes(frame.AsSpan(0, 12), ref data);
            frame[12] = 42;
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(frame), timeout.Token), Is.EqualTo(IOResult.Ok));
            await appChannel.WriteStarted.Task.WaitAsync(timeout.Token);

            await appChannel.CloseAsync();
            YamuxHeader reset = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            Assert.That(reset.Flags, Is.EqualTo(YamuxHeaderFlags.Rst));
            Assert.That(reset.StreamID, Is.EqualTo(syn.StreamID));
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ResetTakesPrecedenceOverFinInOneFrame(bool includesData)
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
        Channel appChannel = new();
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(appChannel);

        TestChannel transport = new();
        Task yamux = new YamuxProtocol().DialAsync(transport, context);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remote = transport.Reverse();
            YamuxHeader syn = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            Task<ReadResult> read = appChannel.Reverse.ReadAsync(1, token: timeout.Token).AsTask();
            byte[] frame = new byte[includesData ? 13 : 12];
            YamuxHeader reset = new()
            {
                Type = includesData ? YamuxHeaderType.Data : YamuxHeaderType.WindowUpdate,
                Flags = YamuxHeaderFlags.Fin | YamuxHeaderFlags.Rst,
                Length = includesData ? 1 : 0,
                StreamID = syn.StreamID
            };
            YamuxHeader.ToBytes(frame.AsSpan(0, 12), ref reset);
            if (includesData)
                frame[12] = 42;

            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(frame), timeout.Token), Is.EqualTo(IOResult.Ok));
            Assert.That((await read.WaitAsync(timeout.Token)).Result, Is.EqualTo(IOResult.Aborted));
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task ResetAbortsBeforeItsDataPayloadArrives()
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
        Channel appChannel = new();
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(appChannel);

        TestChannel transport = new();
        Task yamux = new YamuxProtocol().DialAsync(transport, context);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remote = transport.Reverse();
            YamuxHeader syn = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            Task<ReadResult> read = appChannel.Reverse.ReadAsync(1, token: timeout.Token).AsTask();
            byte[] header = new byte[12];
            YamuxHeader reset = new() { Type = YamuxHeaderType.Data, Flags = YamuxHeaderFlags.Rst, Length = 1, StreamID = syn.StreamID };
            YamuxHeader.ToBytes(header, ref reset);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(header), timeout.Token), Is.EqualTo(IOResult.Ok));
            Assert.That((await read.WaitAsync(timeout.Token)).Result, Is.EqualTo(IOResult.Aborted));

            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 42 }), timeout.Token), Is.EqualTo(IOResult.Ok));
            YamuxHeader ping = new() { Type = YamuxHeaderType.Ping, Flags = YamuxHeaderFlags.Syn, Length = 7 };
            YamuxHeader.ToBytes(header, ref ping);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(header), timeout.Token), Is.EqualTo(IOResult.Ok));
            YamuxHeader ack;
            do
            {
                ack = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
                if (ack.Type != YamuxHeaderType.Ping)
                {
                    Assert.That(ack.Flags, Is.EqualTo(YamuxHeaderFlags.Rst));
                    Assert.That(ack.StreamID, Is.EqualTo(syn.StreamID));
                }
            } while (ack.Type != YamuxHeaderType.Ping);
            Assert.That(ack.Type, Is.EqualTo(YamuxHeaderType.Ping));
            Assert.That(ack.Flags, Is.EqualTo(YamuxHeaderFlags.Ack));
            Assert.That(ack.Length, Is.EqualTo(7));
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestCase((int)YamuxHeaderType.Data, 262145, true, true)]
    [TestCase((int)YamuxHeaderType.Data, -1, false, true)]
    [TestCase((int)YamuxHeaderType.Data, -1, true, true)]
    [TestCase((int)YamuxHeaderType.Data, -1, false, false)]
    [TestCase((int)YamuxHeaderType.Data, 16 * 1024 * 1024 + 1, false, false)]
    [TestCase((int)YamuxHeaderType.WindowUpdate, -1, false, true)]
    [TestCase((int)YamuxHeaderType.WindowUpdate, int.MaxValue, false, true)]
    [TestCase(4, 0, false, true)]
    public async Task MalformedFrameLengthClosesSessionWithProtocolError(
        int type, int length, bool reset, bool knownStream)
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
        Channel appChannel = new();
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(appChannel);

        TestChannel transport = new();
        Task yamux = new YamuxProtocol().DialAsync(transport, context);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remote = transport.Reverse();
            YamuxHeader syn = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            byte[] frame = new byte[12];
            YamuxHeader malformed = new()
            {
                Type = (YamuxHeaderType)type,
                Flags = reset ? YamuxHeaderFlags.Rst : 0,
                Length = length,
                StreamID = knownStream ? syn.StreamID : syn.StreamID + 2
            };
            YamuxHeader.ToBytes(frame, ref malformed);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(frame), timeout.Token), Is.EqualTo(IOResult.Ok));
            YamuxHeader goAway = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            Assert.That(goAway.Type, Is.EqualTo(YamuxHeaderType.GoAway));
            Assert.That(goAway.StreamID, Is.Zero);
            Assert.That(goAway.Length, Is.EqualTo((int)SessionTerminationCode.ProtocolError));
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestCase(0, (int)YamuxHeaderType.Data, 0, 0, 12)]
    [TestCase(0, (int)YamuxHeaderType.WindowUpdate, 0, 0, 0)]
    [TestCase(0, (int)YamuxHeaderType.Ping, 2, (int)YamuxHeaderFlags.Syn, 1)]
    [TestCase(0, (int)YamuxHeaderType.GoAway, 2, 0, 0)]
    [TestCase(0, (int)YamuxHeaderType.WindowUpdate, 3, (int)YamuxHeaderFlags.Syn, 0)]
    [TestCase(1, (int)YamuxHeaderType.Ping, 0, (int)YamuxHeaderFlags.Syn, 0)]
    public async Task MalformedHeaderClosesSessionWithProtocolError(int version, int type, int streamId, int flags, int length)
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
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(new TestChannel());

        TestChannel transport = new();
        Task yamux = new YamuxProtocol().DialAsync(transport, context);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        try
        {
            IChannel remote = transport.Reverse();
            await remote.ReadAsync(12, token: timeout.Token).OrThrow();
            byte[] frame = new byte[12];
            YamuxHeader invalid = new()
            {
                Version = (byte)version,
                Type = (YamuxHeaderType)type,
                Flags = (YamuxHeaderFlags)flags,
                StreamID = streamId,
                Length = length
            };
            YamuxHeader.ToBytes(frame, ref invalid);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(frame), timeout.Token), Is.EqualTo(IOResult.Ok));
            YamuxHeader goAway = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            Assert.That(goAway.Type, Is.EqualTo(YamuxHeaderType.GoAway));
            Assert.That(goAway.Length, Is.EqualTo((int)SessionTerminationCode.ProtocolError));
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task ResetAfterWrappedChannelHalfCloseRemainsAborted()
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
        TestChannel wrapped = new();
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(wrapped);

        TestChannel transport = new();
        Task yamux = new YamuxProtocol().DialAsync(transport, context);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remote = transport.Reverse();
            YamuxHeader syn = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            IChannel application = wrapped.Reverse();
            Assert.That(await application.WriteEofAsync(timeout.Token), Is.EqualTo(IOResult.Ok));
            YamuxHeader fin = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            Assert.That(fin.Flags, Is.EqualTo(YamuxHeaderFlags.Fin));
            Assert.That(fin.StreamID, Is.EqualTo(syn.StreamID));

            byte[] frame = new byte[12];
            YamuxHeader reset = new() { Type = YamuxHeaderType.WindowUpdate, Flags = YamuxHeaderFlags.Rst, StreamID = syn.StreamID };
            YamuxHeader.ToBytes(frame, ref reset);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(frame), timeout.Token), Is.EqualTo(IOResult.Ok));
            Assert.That((await application.ReadAsync(1, token: timeout.Token)).Result, Is.EqualTo(IOResult.Aborted));
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

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
    public async Task BlockedResetWriteDoesNotRetainClosedStream()
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
        Channel appChannel = new();
        TaskCompletionSource upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            upgraded.TrySetResult();
            return appChannel;
        });

        StreamClosedLogger logger = new();
        ILoggerFactory loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        ManualTimeProvider clock = new();
        BlockingWriteChannel transport = new(2);
        Task yamux = new YamuxProtocol(loggerFactory: loggerFactory, timeProvider: clock).DialAsync(transport, context);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            await upgraded.Task.WaitAsync(timeout.Token);
            await appChannel.AbortAsync();
            await transport.WriteBlocked.Task.WaitAsync(timeout.Token);
            var resetTimer = await clock.NextTimerAsync(timeout.Token);
            Assert.That(resetTimer.DueTime, Is.EqualTo(TimeSpan.FromSeconds(10)));
            clock.Advance(TimeSpan.FromSeconds(10));
            resetTimer.Callback(resetTimer.State);
            await logger.Closed.Task.WaitAsync(timeout.Token);
            Assert.That(transport.CancelledWriteCount, Is.EqualTo(1));
        }
        finally
        {
            transport.Release();
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestCase(1, false)]
    [TestCase(2, true)]
    [TestCase(2, false)]
    public async Task ClosedStreamWithBlockedOutboundWriteReleases(int blockedWrite, bool sendData)
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
        ManualTimeProvider clock = new();
        BlockingWriteChannel transport = new(blockedWrite);
        Task yamux = new YamuxProtocol(loggerFactory: loggerFactory, timeProvider: clock,
            closedStreamIdleTimeout: TimeSpan.FromMinutes(5)).DialAsync(transport, context);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel app = appChannel.Reverse();
            if (sendData)
                Assert.That(await app.WriteAsync(new ReadOnlySequence<byte>(new byte[1]), timeout.Token), Is.EqualTo(IOResult.Ok));
            if (blockedWrite == 2 && !sendData)
                await app.CloseAsync();
            await transport.WriteBlocked.Task.WaitAsync(timeout.Token);
            if (blockedWrite == 1 || sendData)
                await app.CloseAsync();
            Assert.That(transport.BlockedWriteToken.CanBeCanceled, Is.True);

            var drainTimer = await clock.NextTimerAsync(timeout.Token);
            Assert.That(drainTimer.DueTime, Is.EqualTo(TimeSpan.FromMinutes(5)));
            clock.Advance(TimeSpan.FromMinutes(5));
            drainTimer.Callback(drainTimer.State);

            await logger.Closed.Task.WaitAsync(timeout.Token);
            Assert.That(transport.CancelledWriteCount, Is.EqualTo(1));
        }
        finally
        {
            transport.Release();
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestCase(true, false, false)]
    [TestCase(false, false, false)]
    [TestCase(true, true, false)]
    [TestCase(false, false, true)]
    public async Task ClosedStreamWithExhaustedRemoteWindowReleases(bool remoteReadsReset, bool grantsLateWindowCredit, bool closeConnection)
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
        ManualTimeProvider clock = new();
        TestChannel transport = new();
        Task yamux = new YamuxProtocol(loggerFactory: loggerFactory, timeProvider: clock,
            closedStreamIdleTimeout: closeConnection ? null : TimeSpan.FromMinutes(5)).DialAsync(transport, context);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remote = transport.Reverse();
            IChannel app = appChannel.Reverse();
            Task<IOResult> upload = app.WriteAsync(new ReadOnlySequence<byte>(new byte[512 * 1024]), timeout.Token).AsTask();

            int received = 0;
            while (received < YamuxProtocol.ProtocolInitialWindowSize)
            {
                YamuxHeader header = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
                if (header.Type == YamuxHeaderType.Data && header.Length > 0)
                {
                    await remote.ReadAsync(header.Length, token: timeout.Token).OrThrow();
                    received += header.Length;
                }
            }

            Assert.That(await upload, Is.EqualTo(IOResult.Ok));
            await app.CloseAsync();
            if (closeConnection)
            {
                await transport.CloseAsync();
                await logger.Closed.Task.WaitAsync(timeout.Token);
                Assert.That(clock.CreatedTimerCount, Is.Zero);
                return;
            }

            var drainTimer = await clock.NextTimerAsync(timeout.Token);
            Assert.That(drainTimer.DueTime, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(logger.Closed.Task.IsCompleted, Is.False);

            if (grantsLateWindowCredit)
            {
                clock.Advance(TimeSpan.FromMinutes(4));
                byte[] grant = new byte[12];
                YamuxHeader grantHeader = new() { Type = YamuxHeaderType.WindowUpdate, Length = 1, StreamID = 1 };
                YamuxHeader.ToBytes(grant, ref grantHeader);
                Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(grant), timeout.Token), Is.EqualTo(IOResult.Ok));
                YamuxHeader data = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
                Assert.That(data.Type, Is.EqualTo(YamuxHeaderType.Data));
                Assert.That(data.Length, Is.EqualTo(1));
                await remote.ReadAsync(1, token: timeout.Token).OrThrow();
                await clock.NextTimestampAtLeastAsync(TimeSpan.FromMinutes(4).Ticks, timeout.Token);
                clock.Advance(TimeSpan.FromMinutes(1));
            }
            else
            {
                clock.Advance(TimeSpan.FromMinutes(5));
            }
            drainTimer.Callback(drainTimer.State);
            if (grantsLateWindowCredit)
            {
                var remainingTimer = await clock.NextTimerAsync(timeout.Token);
                if (remainingTimer.DueTime != TimeSpan.FromMinutes(4))
                {
                    remainingTimer.Callback(remainingTimer.State);
                    await logger.Closed.Task.WaitAsync(timeout.Token);
                }
                Assert.That(remainingTimer.DueTime, Is.EqualTo(TimeSpan.FromMinutes(4)));
                Assert.That(logger.Closed.Task.IsCompleted, Is.False);
                clock.Advance(TimeSpan.FromMinutes(4));
                remainingTimer.Callback(remainingTimer.State);
            }
            var resetTimer = await clock.NextTimerAsync(timeout.Token);
            Assert.That(resetTimer.DueTime, Is.EqualTo(TimeSpan.FromSeconds(10)));
            if (remoteReadsReset)
            {
                YamuxHeader reset = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
                Assert.That(reset.Type, Is.EqualTo(YamuxHeaderType.WindowUpdate));
                Assert.That(reset.Flags, Is.EqualTo(YamuxHeaderFlags.Rst));
                Assert.That(reset.StreamID, Is.EqualTo(1));
            }
            else
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                resetTimer.Callback(resetTimer.State);
            }
            await logger.Closed.Task.WaitAsync(timeout.Token);
            byte[] update = new byte[12];
            YamuxHeader updateHeader = new() { Type = YamuxHeaderType.WindowUpdate, Length = 1, StreamID = 1 };
            YamuxHeader.ToBytes(update, ref updateHeader);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(update), timeout.Token), Is.EqualTo(IOResult.Ok));
            await logger.Ignored.Task.WaitAsync(timeout.Token);
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task EnlargedInitialReceiveWindowIsAdvertisedToPeer(bool enlargeDialer)
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
        YamuxWindowSettings settings = new()
        {
            InitialWindowSize = 1024 * 1024,
            UseDynamicWindow = false
        };
        YamuxProtocol dialer = enlargeDialer ? new(windowSettings: settings) : new();
        YamuxProtocol listener = enlargeDialer ? new() : new(windowSettings: settings);
        Task listen = listener.ListenAsync(transport.Reverse(), listenerContext);
        Task dial = dialer.DialAsync(transport, dialerContext);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        try
        {
            byte[] request = new byte[512 * 1024];
            new Random(12345).NextBytes(request);
            IChannel sender = enlargeDialer ? listenerAppChannel.Reverse() : dialerAppChannel.Reverse();
            IChannel receiver = enlargeDialer ? dialerAppChannel.Reverse() : listenerAppChannel.Reverse();
            Task<IOResult> send = sender.WriteAsync(new ReadOnlySequence<byte>(request), timeout.Token).AsTask();
            ReadOnlySequence<byte> received = await receiver.ReadAsync(request.Length, token: timeout.Token).OrThrow();
            Assert.That(received.ToArray(), Is.EqualTo(request));
            Assert.That(await send.WaitAsync(timeout.Token), Is.EqualTo(IOResult.Ok));
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
        public TaskCompletionSource Ignored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (message.EndsWith("stream 1: Closed", StringComparison.Ordinal))
                Closed.TrySetResult();
            if (message.Contains("Stream 1: Ignored for closed stream", StringComparison.Ordinal))
                Ignored.TrySetResult();
        }
    }

    private sealed class ObservedInboundChannel : IChannel
    {
        private readonly Channel _inner = new();
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskAwaiter GetAwaiter() => _inner.GetAwaiter();
        public ValueTask CloseAsync() => _inner.CloseAsync();
        public ValueTask AbortAsync() => _inner.AbortAsync();
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
            CancellationToken token = default) => _inner.ReadAsync(length, blockingMode, token);
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            ValueTask<IOResult> write = _inner.WriteAsync(bytes, token);
            WriteStarted.TrySetResult();
            return write;
        }
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => _inner.WriteEofAsync(token);
    }

    private sealed class BlockingWriteChannel(int blockedWrite) : IChannel
    {
        private readonly TestChannel _inner = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;
        private int _cancelledWriteCount;
        private CancellationToken _blockedWriteToken;

        public TaskCompletionSource WriteBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken BlockedWriteToken => _blockedWriteToken;
        public int CancelledWriteCount => Volatile.Read(ref _cancelledWriteCount);

        public TaskAwaiter GetAwaiter() => _inner.GetAwaiter();
        public IChannel Reverse() => _inner.Reverse();
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
            CancellationToken token = default) => _inner.ReadAsync(length, blockingMode, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => _inner.WriteEofAsync(token);
        public ValueTask CloseAsync() => _inner.CloseAsync();
        public ValueTask AbortAsync() => _inner.AbortAsync();

        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref _writeCount) != blockedWrite)
                return IOResult.Ok;

            _blockedWriteToken = token;
            WriteBlocked.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(token);
                return IOResult.Ok;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelledWriteCount);
                return IOResult.Cancelled;
            }
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly global::System.Threading.Channels.Channel<(TimerCallback Callback, object? State, TimeSpan DueTime)> _timers =
            global::System.Threading.Channels.Channel.CreateUnbounded<(TimerCallback, object?, TimeSpan)>();
        private readonly global::System.Threading.Channels.Channel<long> _timestamps =
            global::System.Threading.Channels.Channel.CreateUnbounded<long>();
        private long _timestamp;
        private int _createdTimerCount;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public int CreatedTimerCount => Volatile.Read(ref _createdTimerCount);
        public override long GetTimestamp()
        {
            long timestamp = Volatile.Read(ref _timestamp);
            _timestamps.Writer.TryWrite(timestamp);
            return timestamp;
        }

        public void Advance(TimeSpan by) => Interlocked.Add(ref _timestamp, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _createdTimerCount);
            _timers.Writer.TryWrite((callback, state, dueTime));
            return Substitute.For<ITimer>();
        }

        public ValueTask<(TimerCallback Callback, object? State, TimeSpan DueTime)> NextTimerAsync(CancellationToken token) =>
            _timers.Reader.ReadAsync(token);

        public async Task NextTimestampAtLeastAsync(long timestamp, CancellationToken token)
        {
            while (await _timestamps.Reader.ReadAsync(token) < timestamp) { }
        }
    }
}
