// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Tls;

namespace DataTransferBenchmark;

public sealed class PerfPeerFactoryBuilder(IServiceProvider? serviceProvider = null)
    : PeerFactoryBuilderBase<PerfPeerFactoryBuilder, PeerFactory>(serviceProvider)
{
    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef tcp = Get<IpTcpProtocol>();
        ProtocolRef[] appSelector = [Get<MultistreamProtocol>()];

        Connect([tcp], [Get<MultistreamProtocol>()], [Get<NoiseProtocol>(), Get<TlsProtocol>()],
            [Get<MultistreamProtocol>()], [Get<YamuxProtocol>()], appSelector);

        ProtocolRef quic = Get<QuicProtocol>();
        Connect([quic], appSelector);
        // Perf peers only advertise the benchmark protocol, not Identify or Ping.
        Connect(appSelector, [.. additionalProtocols]);

        return [tcp, quic];
    }
}
