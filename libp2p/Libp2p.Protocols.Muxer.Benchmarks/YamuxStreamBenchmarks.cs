// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using BenchmarkDotNet.Attributes;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Muxer.Benchmarks;

/// <summary>
/// One Yamux stream between two in-memory Yamux sessions. Each operation moves
/// <see cref="TransferBytes"/> from the dialer application to the listener application,
/// which covers the outbound DATA frame path, the inbound DATA delivery path and window updates.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BenchmarkConfig))]
public class YamuxStreamBenchmarks
{
    private const int TransferBytes = 1024 * 1024;

    private readonly CancellationTokenSource _lifetime = new();
    private Channel _transport = null!;
    private Task _dial = null!;
    private Task _listen = null!;
    private IChannel _dialerApp = null!;
    private IChannel _listenerApp = null!;
    private byte[] _payload = null!;

    [Params(1024, 32 * 1024)]
    public int WriteSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[WriteSize];
        Random.Shared.NextBytes(_payload);

        Channel dialerUp = new();
        Channel listenerUp = new();
        TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BenchmarkContext dialer = new("dialer", _lifetime.Token, [new UpgradeOptions()], _ => dialerUp);
        BenchmarkContext listener = new("listener", _lifetime.Token, upgrade: _ =>
        {
            accepted.TrySetResult();
            return listenerUp;
        });

        _transport = new Channel();
        YamuxProtocol yamux = new();
        _listen = yamux.ListenAsync(_transport.Reverse, listener);
        _dial = yamux.DialAsync(_transport, dialer);
        accepted.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

        _dialerApp = dialerUp.Reverse;
        _listenerApp = listenerUp.Reverse;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _transport.CloseAsync().AsTask().GetAwaiter().GetResult();
        _lifetime.Cancel();
        Task.WhenAll(_dial, _listen).WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
        _lifetime.Dispose();
    }

    [Benchmark]
    public async Task Upload()
    {
        Task received = ReceiveAsync(_listenerApp, TransferBytes);
        for (int sent = 0; sent < TransferBytes; sent += WriteSize)
        {
            await _dialerApp.WriteAsync(new ReadOnlySequence<byte>(_payload)).OrThrow();
        }
        await received;
    }

    private static async Task ReceiveAsync(IChannel channel, int length)
    {
        for (long received = 0; received < length;)
        {
            received += (await channel.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow()).Length;
        }
    }
}
