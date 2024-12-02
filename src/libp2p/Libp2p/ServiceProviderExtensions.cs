// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Libp2p.Stack;

public static class ServiceProviderExtensions
{
    [RequiresPreviewFeatures]
    public static IServiceCollection AddLibp2p(this IServiceCollection services, Func<ILibp2pPeerFactoryBuilder, IPeerFactoryBuilder>? factorySetup = null)
    {
        return services
            .AddSingleton(sp => new Libp2pPeerFactoryBuilder(sp))
            .AddSingleton<IProtocolStackSettings, ProtocolStackSettings>()
            .AddSingleton(sp => factorySetup is null ? sp.GetRequiredService<Libp2pPeerFactoryBuilder>() : factorySetup(sp.GetRequiredService<Libp2pPeerFactoryBuilder>()))
            .AddSingleton(sp => (ILibp2pPeerFactoryBuilder)sp.GetRequiredService<IPeerFactoryBuilder>())
            .AddScoped(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
            .AddScoped<MultiplexerSettings>()
            .AddScoped<PubsubRouter>()
            .AddScoped<PeerStore>()
            .AddScoped<MDnsDiscoveryProtocol>()
            ;
    }
}
