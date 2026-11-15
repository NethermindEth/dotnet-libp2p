// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p;

public static class WebSocketPeerFactoryBuilderExtensions
{
    public static ILibp2pPeerFactoryBuilder WithWebSockets(this ILibp2pPeerFactoryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithStreamTransport<WebSocketProtocol>();
    }
}
