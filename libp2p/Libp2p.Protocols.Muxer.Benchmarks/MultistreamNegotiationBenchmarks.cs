// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Muxer.Benchmarks;

/// <summary>
/// One multistream-select negotiation over a link with a fixed one-way delay.
/// <see cref="Listed"/> is the connection upgrade path, where the dialer proposes from its protocol list;
/// <see cref="Selected"/> is the stream path, where the dialer already knows the protocol.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BenchmarkConfig))]
public class MultistreamNegotiationBenchmarks
{
    private static readonly IProtocol Proposed = new NamedProtocol("/yamux/1.0.0");

    [Params(0, 5)]
    public int OneWayDelayMs { get; set; }

    [Benchmark]
    public Task Listed() => NegotiateAsync(new UpgradeOptions());

    [Benchmark]
    public Task Selected() => NegotiateAsync(new UpgradeOptions { SelectedProtocol = Proposed });

    private async Task NegotiateAsync(UpgradeOptions dialOptions)
    {
        (DelayedPipe.End dialerEnd, DelayedPipe.End listenerEnd) = DelayedPipe.Create(TimeSpan.FromMilliseconds(OneWayDelayMs));
        MultistreamProtocol multistream = new();
        BenchmarkContext dialer = new("dialer", CancellationToken.None, subProtocols: [Proposed], upgradeOptions: dialOptions);
        BenchmarkContext listener = new("listener", CancellationToken.None, subProtocols: [Proposed]);
        await Task.WhenAll(multistream.ListenAsync(listenerEnd, listener), multistream.DialAsync(dialerEnd, dialer));
    }

    private sealed class NamedProtocol(string id) : IProtocol
    {
        public string Id => id;
    }
}
