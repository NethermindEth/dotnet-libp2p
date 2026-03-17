// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.WebRtc.Extensions;

public static class ServiceCollectionExtensions
{
    public static ILibp2pPeerFactoryBuilder AddWebRtcDirect(this ILibp2pPeerFactoryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<WebRtcDirectProtocol>();
        builder.WithWebRtcDirect();
        return builder;
    }
}