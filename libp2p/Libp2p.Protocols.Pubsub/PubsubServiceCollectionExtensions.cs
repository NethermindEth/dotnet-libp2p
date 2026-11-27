// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public static class PubsubServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default singleton <see cref="PubsubRouter"/> when the collection has no router registration.
    /// The router uses the registered peer store and optional <see cref="PubsubSettings"/>.
    /// Select the standard stack handlers with <c>WithPubsub()</c> and start the router with
    /// <see cref="PubsubRouter.StartAsync"/>.
    /// </summary>
    public static IServiceCollection AddPubsub(this IServiceCollection services)
    {
        services.TryAddSingleton<PubsubRouter>();
        return services;
    }
}
