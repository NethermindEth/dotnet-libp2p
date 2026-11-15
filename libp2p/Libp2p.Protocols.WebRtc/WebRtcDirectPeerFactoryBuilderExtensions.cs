// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc;

namespace Nethermind.Libp2p;

public static class WebRtcDirectPeerFactoryBuilderExtensions
{
    public static ILibp2pPeerFactoryBuilder WithWebRtcDirect(this ILibp2pPeerFactoryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithDatagramTransport<WebRtcDirectProtocol>();
    }
}
