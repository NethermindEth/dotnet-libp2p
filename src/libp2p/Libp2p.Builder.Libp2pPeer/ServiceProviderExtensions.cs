// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Builder;

public static class ServiceProviderExtensions
{
    public static IServiceCollection AddLibp2pBuilder(this IServiceCollection services)
    {
        return services
            .AddSingleton<IPeerFactoryBuilder, Libp2pPeerFactoryBuilder>();
    }
}
