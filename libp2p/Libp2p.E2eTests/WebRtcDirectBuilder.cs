// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc;

namespace Libp2p.E2eTests;

internal sealed class WebRtcDirectBuilder(IServiceProvider? provider) : Libp2pPeerFactoryBuilder(provider)
{
    protected override ProtocolRef[] DatagramTransports() => [.. base.DatagramTransports(), Get<WebRtcDirectProtocol>()];
}
