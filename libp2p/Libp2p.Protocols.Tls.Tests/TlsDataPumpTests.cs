// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Tls;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class TlsDataPumpTests
{
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public async Task DeliversWritesOfEverySegmentLayoutUnchanged(int segments)
    {
        await using TlsPair pair = await TlsPair.StartAsync();
        byte[] expected = new byte[100_000];
        new Random(segments).NextBytes(expected);
        ReadOnlySequence<byte> data = new(expected.AsMemory(0, expected.Length / segments).ToArray());
        int offset = expected.Length / segments;
        for (int i = 1; i < segments; i++)
        {
            int end = i == segments - 1 ? expected.Length : offset + expected.Length / segments;
            data = data.Append(expected.AsMemory(offset, end - offset).ToArray());
            offset = end;
        }

        Task<IOResult> write = Task.Run(async () =>
        {
            IOResult result = await pair.Sender.WriteAsync(data);
            return result == IOResult.Ok ? await pair.Sender.WriteEofAsync() : result;
        });
        List<byte> received = [];
        while (true)
        {
            ReadResult read = await pair.Receiver.ReadAsync(0, ReadBlockingMode.WaitAny).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            if (read.Result != IOResult.Ok) break;
            received.AddRange(read.Data.ToArray());
        }

        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(IOResult.Ok));
        Assert.That(received, Is.EqualTo(expected));
    }

    [Test]
    public async Task PublishesReceivedRecordsLargerThan1024Bytes()
    {
        await using TlsPair pair = await TlsPair.StartAsync();
        byte[] payload = new byte[8 * 1024];

        ValueTask<IOResult> write = pair.Sender.WriteAsync(new ReadOnlySequence<byte>(payload));
        long largest = 0;
        for (long received = 0; received < payload.Length;)
        {
            long length = (await pair.Receiver.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow().AsTask().WaitAsync(TimeSpan.FromSeconds(15))).Length;
            largest = Math.Max(largest, length);
            received += length;
        }

        Assert.That(await write.AsTask().WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(IOResult.Ok));
        Assert.That(largest, Is.EqualTo(payload.Length));
    }

    private sealed class TlsPair : IAsyncDisposable
    {
        private readonly TestChannel _down;
        private readonly TestChannel _dialerUp;
        private readonly TestChannel _listenerUp;
        private readonly Task _dial;
        private readonly Task _listen;

        private TlsPair(TestChannel down, TestChannel dialerUp, TestChannel listenerUp, Task dial, Task listen)
        {
            _down = down;
            _dialerUp = dialerUp;
            _listenerUp = listenerUp;
            _dial = dial;
            _listen = listen;
        }

        public IChannel Sender => _dialerUp.Reverse();
        public IChannel Receiver => _listenerUp.Reverse();

        public static async Task<TlsPair> StartAsync()
        {
            TestChannel down = new();
            TestChannel dialerUp = new();
            TestChannel listenerUp = new();
            TaskCompletionSource dialerUpgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource listenerUpgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);

            IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
            dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
            dialerContext.State.Returns(new State { RemoteAddress = $"/ip4/127.0.0.1/tcp/0/p2p/{TestPeers.PeerId(2)}" });
            dialerContext.SubProtocols.Returns(Array.Empty<IProtocol>());
            dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
            {
                dialerUpgraded.SetResult();
                return dialerUp;
            });

            IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
            listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));
            listenerContext.State.Returns(new State { RemoteAddress = (Multiaddress)"/ip4/127.0.0.1/tcp/0" });
            listenerContext.SubProtocols.Returns(Array.Empty<IProtocol>());
            listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
            {
                listenerUpgraded.SetResult();
                return listenerUp;
            });

            Task listen = new TlsProtocol().ListenAsync(down, listenerContext);
            Task dial = new TlsProtocol().DialAsync(down.Reverse(), dialerContext);
            Task upgraded = Task.WhenAll(dialerUpgraded.Task, listenerUpgraded.Task);
            if (await Task.WhenAny(listen, dial, upgraded).WaitAsync(TimeSpan.FromSeconds(15)) != upgraded)
            {
                await Task.WhenAll(dial, listen).WaitAsync(TimeSpan.FromSeconds(15));
            }
            await upgraded.WaitAsync(TimeSpan.FromSeconds(15));
            return new TlsPair(down, dialerUp, listenerUp, dial, listen);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.WhenAll(
                _dialerUp.CloseAsync().AsTask(),
                _listenerUp.CloseAsync().AsTask(),
                _down.CloseAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(_dial, _listen).WaitAsync(TimeSpan.FromSeconds(15));
        }
    }
}
