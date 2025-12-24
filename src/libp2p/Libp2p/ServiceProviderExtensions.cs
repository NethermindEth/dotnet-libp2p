// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Libp2p;

public static class ServiceProviderExtensions
{
    public static IServiceCollection AddLibp2p(this IServiceCollection services, Func<ILibp2pPeerFactoryBuilder, IPeerFactoryBuilder>? factorySetup = null)
    {
        return services.AddLibp2p<Libp2pPeerFactoryBuilder>(factorySetup is null ? null : new Func<IPeerFactoryBuilder, IPeerFactoryBuilder>(b => factorySetup((ILibp2pPeerFactoryBuilder)b)))
            .AddSingleton(sp => (ILibp2pPeerFactoryBuilder)sp.GetRequiredService<IPeerFactoryBuilder>());
    }

    public static IServiceCollection AddLibp2p<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPeerFactoryBuilder>(this IServiceCollection services, Func<IPeerFactoryBuilder, IPeerFactoryBuilder>? factorySetup = null)
        where TPeerFactoryBuilder : IPeerFactoryBuilder
    {
        return services
            .AddSingleton<IProtocolStackSettings, ProtocolStackSettings>()
            .AddSingleton(sp => factorySetup is null ? ActivatorUtilities.CreateInstance<TPeerFactoryBuilder>(sp) : factorySetup(ActivatorUtilities.CreateInstance<TPeerFactoryBuilder>(sp)))
            .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
            .AddSingleton<MultiplexerSettings>()
            .AddSingleton<PubsubRouter>()
            .AddSingleton<PeerStore>()
            .AddSingleton<MDnsDiscoveryProtocol>()
            .AddSingleton<IdentifyNotifier>()
            ;
    }
}
