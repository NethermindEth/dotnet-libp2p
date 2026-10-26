// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Tls;

namespace Nethermind.Libp2p.Transport.Benchmarks;

public sealed class BenchmarkPeerFactoryBuilder(IServiceProvider? serviceProvider = null)
    : PeerFactoryBuilderBase<BenchmarkPeerFactoryBuilder, PeerFactory>(serviceProvider)
{
    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef tcp = Get<IpTcpProtocol>();
        ProtocolRef quic = Get<QuicProtocol>();
        ProtocolRef[] appSelector = [Get<MultistreamProtocol>()];
        ProtocolRef security = Environment.GetEnvironmentVariable("BENCHMARK_SECURITY") switch
        {
            "noise" => Get<NoiseProtocol>(),
            "tls" => Get<TlsProtocol>(),
            _ => throw new InvalidOperationException("BENCHMARK_SECURITY must be noise or tls")
        };

        Connect([tcp], [Get<MultistreamProtocol>()], [security],
            [Get<MultistreamProtocol>()], [Get<YamuxProtocol>()], appSelector);
        Connect([quic], appSelector);
        Connect(appSelector, [.. additionalProtocols]);

        return [tcp, quic];
    }
}
