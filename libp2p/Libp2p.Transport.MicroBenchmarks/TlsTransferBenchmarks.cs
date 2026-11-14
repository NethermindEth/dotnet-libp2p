// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using BenchmarkDotNet.Attributes;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Tls;
using NSubstitute;

namespace Nethermind.Libp2p.Transport.MicroBenchmarks;

[MemoryDiagnoser]
public class TlsTransferBenchmarks
{
    private const int TransferBytes = 1024 * 1024;
    private const int HeaderLength = 12;
    private const int PayloadLength = 16 * 1024;

    private Channel _down = null!;
    private Channel _dialerUp = null!;
    private Channel _listenerUp = null!;
    private IChannel _sender = null!;
    private IChannel _receiver = null!;
    private Task _dial = null!;
    private Task _listen = null!;
    private ReadOnlySequence<byte> _chunk;

    /// <summary>1: one array per write. 2: a separate frame header in front of the payload, as a multiplexer writes it.</summary>
    [Params(1, 2)]
    public int Segments { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _chunk = Segments == 1
            ? new ReadOnlySequence<byte>(new byte[HeaderLength + PayloadLength])
            : new ReadOnlySequence<byte>(new byte[PayloadLength]).Prepend(new byte[HeaderLength]);

        _down = new Channel();
        _dialerUp = new Channel();
        _listenerUp = new Channel();
        Identity dialerIdentity = new();
        Identity listenerIdentity = new();
        TaskCompletionSource dialerUpgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource listenerUpgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(dialerIdentity);
        dialerContext.State.Returns(new State { RemoteAddress = $"/ip4/127.0.0.1/tcp/0/p2p/{listenerIdentity.PeerId}" });
        dialerContext.SubProtocols.Returns(Array.Empty<IProtocol>());
        dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            dialerUpgraded.SetResult();
            return _dialerUp;
        });

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        listenerContext.Peer.Identity.Returns(listenerIdentity);
        listenerContext.State.Returns(new State { RemoteAddress = (Multiaddress)"/ip4/127.0.0.1/tcp/0" });
        listenerContext.SubProtocols.Returns(Array.Empty<IProtocol>());
        listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            listenerUpgraded.SetResult();
            return _listenerUp;
        });

        _listen = new TlsProtocol().ListenAsync(_down, listenerContext);
        _dial = new TlsProtocol().DialAsync(_down.Reverse, dialerContext);
        Task.WhenAll(dialerUpgraded.Task, listenerUpgraded.Task).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        _sender = _dialerUp.Reverse;
        _receiver = _listenerUp.Reverse;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _dialerUp.CloseAsync().AsTask().GetAwaiter().GetResult();
        _listenerUp.CloseAsync().AsTask().GetAwaiter().GetResult();
        _down.CloseAsync().AsTask().GetAwaiter().GetResult();
        Task.WhenAll(_dial, _listen).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task<long> Transfer1MiB()
    {
        Task write = Task.Run(async () =>
        {
            for (int sent = 0; sent < TransferBytes; sent += (int)_chunk.Length)
            {
                await _sender.WriteAsync(_chunk);
            }
        });

        long target = (TransferBytes + _chunk.Length - 1) / _chunk.Length * _chunk.Length;
        long received = 0;
        while (received < target)
        {
            received += (await _receiver.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow()).Length;
        }

        await write;
        return received;
    }
}
