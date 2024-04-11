// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using NUnit.Framework.Internal;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

[TestFixture]
public class YamuxProtocolTests
{
    // implement the following testcases:
    // Establish connection, expect 0 stream
    // Close connection, expect goaway
    // Try speak a protocol
    // Exchange data
    // Expect error and react to it

    //[Test]
    //public async Task Test_Connection_Ackowledged()
    //{
    //    IChannel downChannel = new TestChannel();
    //    IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
    //    IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
    //    IPeerContext peerContext = Substitute.For<IPeerContext>();

    //    IProtocol? proto1 = Substitute.For<IProtocol>();
    //    proto1.Id.Returns("proto1");
    //    channelFactory.SubProtocols.Returns(new[] { proto1 });
    //    IChannel upChannel = new TestChannel();
    //    channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
    //        .Returns(upChannel);

    //    YamuxProtocol proto = new();
    //    _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
    //    await downChannel.WriteLineAsync(proto.Id);
    //    await downChannel.WriteLineAsync("proto1");

    //    Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
    //    Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));
    //    channelFactory.Received().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto1);
    //    await downChannel.CloseAsync();
    //}

    //[Test]
    //public async Task Test_ConnectionClosed_ForBrokenHandshake()
    //{
    //    // IChannel downChannel = new TestChannel();
    //    // IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
    //    // IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
    //    // IPeerContext peerContext = Substitute.For<IPeerContext>();
    //    //
    //    // IProtocol? proto1 = Substitute.For<IProtocol>();
    //    // proto1.Id.Returns("proto1");
    //    // channelFactory.SubProtocols.Returns(new[] { proto1 });
    //    // IChannel upChannel = new TestChannel();
    //    // channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
    //    //     .Returns(upChannel);
    //    //
    //    // NoiseProtocol proto = new();
    //    // _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
    //    // await downChannel.WriteLineAsync(proto.Id);
    //    // await downChannel.WriteLineAsync("proto2");
    //    //
    //    // Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
    //    // Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));
    //    // channelFactory.DidNotReceive().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto1);
    //    // await upChannel.CloseAsync();
    //}

    [Test]
    public async Task Test_Connection_Ackowledged()
    {
        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        IPeerContext dialerPeerContext = Substitute.For<IPeerContext>();
        var dialerRequests = new BlockingCollection<IChannelRequest>() { new ChannelRequest() { SubProtocol = proto1 } };
        dialerPeerContext.SubDialRequests.Returns(dialerRequests);

        TestChannel dialerDownChannel = new TestChannel();
        IChannelFactory dialerUpchannelFactory = Substitute.For<IChannelFactory>();
        dialerUpchannelFactory.SubProtocols.Returns(new[] { proto1 });
        TestChannel dialerUpChannel = new TestChannel();
        dialerUpchannelFactory.SubDial(Arg.Any<IPeerContext>(), Arg.Any<IChannelRequest>())
           .Returns(dialerUpChannel);


        _ = dialerUpChannel.Reverse().WriteLineAsync("hello").AsTask().ContinueWith((e) => dialerUpChannel.CloseAsync());

        IPeerContext listenerPeerContext = Substitute.For<IPeerContext>();
        IChannel listenerDownChannel = dialerDownChannel.Reverse();
        IChannelFactory listenerUpchannelFactory = Substitute.For<IChannelFactory>();
        var listenerRequests = new BlockingCollection<IChannelRequest>();
        listenerPeerContext.SubDialRequests.Returns(listenerRequests);
        listenerUpchannelFactory.SubProtocols.Returns(new[] { proto1 });
        TestChannel listenerUpChannel = new TestChannel();
        listenerUpchannelFactory.SubListen(Arg.Any<IPeerContext>(), Arg.Any<IChannelRequest>())
           .Returns(listenerUpChannel);

        YamuxProtocol proto = new(new DebugLoggerFactory());

        _ = proto.ListenAsync(listenerDownChannel, listenerUpchannelFactory, listenerPeerContext);

        _ = proto.DialAsync(dialerDownChannel, dialerUpchannelFactory, dialerPeerContext);


        var res = await listenerUpChannel.Reverse().ReadLineAsync();
        //await listenerUpChannel.CloseAsync();

        Assert.That(res, Is.EqualTo("hello"));

        await Task.Delay(1000);
        //IChannel listenerUpChannel = new TestChannel();


        //
        //_ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
        //await downChannel.WriteLineAsync(proto.Id);
        //await downChannel.WriteLineAsync("proto1");

        //Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        //Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));
        //channelFactory.Received().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto1);
        //await downChannel.CloseAsync();
    }


    class PingPongProtocol : IProtocol
    {
        public string Id => throw new NotImplementedException();

        public async Task DialAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
        {
            const string line = "hello";
            await downChannel.WriteLineAsync(line);
            await downChannel.WriteEofAsync();
            string received = await downChannel.ReadLineAsync();
            Assert.That(received, Is.EqualTo(line));
        }

        public async Task ListenAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
        {
            string line = await downChannel.ReadLineAsync();
            ReadOnlySequence<byte>? readAfter = await downChannel.ReadAsync(0, ReadBlockingMode.WaitAny);
            Assert.That(readAfter, Is.Null);
            await downChannel.WriteLineAsync(line);
        }
    }
}

internal class DebugLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
{
    class DebugLogger : Microsoft.Extensions.Logging.ILogger, IDisposable
    {
        private string categoryName;

        public DebugLogger(string categoryName)
        {
            this.categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return this;
        }

        public void Dispose()
        {
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            TestContext.Out.WriteLine($"{logLevel} {categoryName}:{eventId}: {(exception is null ? state?.ToString() : formatter(state, exception))}");
        }
    }

    public void AddProvider(ILoggerProvider provider)
    {
       
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
    {
        return new DebugLogger(categoryName);
    }

    public void Dispose()
    {
        
    }
}
