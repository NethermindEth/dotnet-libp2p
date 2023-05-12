// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.Floodsub;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p.Builder;

public static class ServiceProviderExtensions
{
    public static IServiceCollection AddLibp2p(this IServiceCollection services, Func<ILibp2pPeerFactoryBuilder, IPeerFactoryBuilder> factorySetup)
    {
        return services
            .AddScoped<IPeerFactoryBuilder>((sp) => factorySetup(new Libp2pPeerFactoryBuilder(sp)))
            .AddScoped<ILibp2pPeerFactoryBuilder>((sp) => (ILibp2pPeerFactoryBuilder)factorySetup(new Libp2pPeerFactoryBuilder(sp)))
            .AddScoped<IPeerFactory>((sp) => sp.GetService<IPeerFactoryBuilder>()!.Build())
            .AddScoped<PeerRegistry>()
            .AddScoped<FloodsubRouter>()
            //.AddScoped<GossipsubRouter>()
            //.AddScoped<GossipsubRouterV11>()
            ;
    }
}
