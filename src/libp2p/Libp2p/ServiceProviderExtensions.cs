// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using System.Runtime.Versioning;

namespace Nethermind.Libp2p.Stack;

public static class ServiceProviderExtensions
{
    public static IServiceCollection AddLibp2p(this IServiceCollection services, Func<ILibp2pPeerFactoryBuilder, IPeerFactoryBuilder> factorySetup)
    {
        return services
            .AddScoped(sp => factorySetup(new Libp2pPeerFactoryBuilder(sp)))
            .AddScoped(sp => (ILibp2pPeerFactoryBuilder)factorySetup(new Libp2pPeerFactoryBuilder(sp)))
            .AddScoped(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
            .AddScoped<PubsubRouter>()
            .AddScoped<MultiplexerSettings>()
            ;
    }
}
