// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Builder;

public static class ServiceProviderExtensions
{
    public static IServiceCollection AddLibp2p(this IServiceCollection services, Func<IPeerFactoryBuilder, IPeerFactoryBuilder> factorySetup)
    {
        return services
            .AddSingleton<IPeerFactoryBuilder>((sp) =>  factorySetup(new Libp2pPeerFactoryBuilder(sp)))
            .AddSingleton<IPeerFactory>((sp) => sp.GetService<IPeerFactoryBuilder>()!.Build());
    }
}
