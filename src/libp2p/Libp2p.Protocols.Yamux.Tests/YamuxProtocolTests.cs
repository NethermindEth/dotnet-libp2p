// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Reflection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Yamux;
using NSubstitute;
using NUnit.Framework.Internal;

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

// TODO: Add tests
[TestFixture]
public class YamuxProtocolTests
{
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

    [Test]
    public async Task WriteHeaderAsync_data_without_headroom_falls_back_by_default()
    {
        YamuxProtocol proto = new(windowSettings: new YamuxWindowSettings());
        CapturingWriter writer = new();

        using PooledBuffer.Slice data = PooledBuffer.RentSlice(3);
        await InvokeWriteHeaderAsync(proto, writer, new YamuxHeader { Type = YamuxHeaderType.Data }, data);

        Assert.That(writer.MultiSliceWrites, Is.EqualTo(1));
        Assert.That(writer.BufferWrites, Is.EqualTo(0));
    }

    [Test]
    public void WriteHeaderAsync_data_without_headroom_throws_when_required()
    {
        YamuxProtocol proto = new(windowSettings: new YamuxWindowSettings { RequireDataFrameHeadroom = true });
        CapturingWriter writer = new();

        using PooledBuffer.Slice data = PooledBuffer.RentSlice(3);
        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await InvokeWriteHeaderAsync(proto, writer, new YamuxHeader { Type = YamuxHeaderType.Data }, data));

        Assert.That(exception?.Message, Does.Contain("headroom"));
        Assert.That(writer.BufferWrites + writer.MultiSliceWrites, Is.EqualTo(0));
    }

    [Test]
    public async Task WriteHeaderAsync_header_only_writes_header_when_data_headroom_is_required()
    {
        YamuxProtocol proto = new(windowSettings: new YamuxWindowSettings { RequireDataFrameHeadroom = true });
        CapturingWriter writer = new();

        await InvokeWriteHeaderAsync(proto, writer, new YamuxHeader { Type = YamuxHeaderType.Ping });

        Assert.That(writer.BufferWrites, Is.EqualTo(1));
        Assert.That(writer.MultiSliceWrites, Is.EqualTo(0));
    }

    [Test]
    public async Task WriteHeaderAsync_data_with_headroom_writes_single_frame_when_required()
    {
        YamuxProtocol proto = new(windowSettings: new YamuxWindowSettings { RequireDataFrameHeadroom = true });
        CapturingWriter writer = new();

        using PooledBuffer.Slice data = PooledBuffer.RentSlice(3, headroom: 12);
        await InvokeWriteHeaderAsync(proto, writer, new YamuxHeader { Type = YamuxHeaderType.Data }, data);

        Assert.That(writer.BufferWrites, Is.EqualTo(1));
        Assert.That(writer.MultiSliceWrites, Is.EqualTo(0));
    }

    private static async Task InvokeWriteHeaderAsync(YamuxProtocol protocol, IWriter writer, YamuxHeader header, PooledBuffer.Slice data = default)
    {
        MethodInfo method = typeof(YamuxProtocol).GetMethod("WriteHeaderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ValueTask task = (ValueTask)method.Invoke(protocol, ["test", writer, header, data])!;
        await task;
    }

    private sealed class CapturingWriter : IWriter
    {
        public int BufferWrites { get; private set; }
        public int MultiSliceWrites { get; private set; }

        public ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
        {
            BufferWrites++;
            return new ValueTask<IOResult>(IOResult.Ok);
        }

        public ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default)
        {
            MultiSliceWrites++;
            return new ValueTask<IOResult>(IOResult.Ok);
        }

        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
        {
            return new ValueTask<IOResult>(IOResult.Ok);
        }
    }
}
