// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

public static class SessionRecoveryExtensions
{
    /// <summary>Enables peer-owned recovery for sessions explicitly watched by applications.</summary>
    public static TBuilder WithSessionRecovery<TBuilder>(this TBuilder builder, SessionRecoveryOptions? options = null)
        where TBuilder : IPeerFactoryBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder is not IPeerServiceBuilder services)
            throw new NotSupportedException("The builder does not support peer-owned services.");

        services.AddPeerService<ISessionRecovery>((peer, provider) => new SessionRecovery(peer, options ?? SessionRecoveryOptions.Default,
            (peer as LocalPeer)?.Clock ?? provider.GetService<TimeProvider>() ?? TimeProvider.System));
        return builder;
    }

    /// <summary>Gets the recovery service configured for this local peer.</summary>
    public static ISessionRecovery GetSessionRecovery(this ILocalPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return (peer as IPeerServices)?.GetPeerService<ISessionRecovery>()
            ?? throw new InvalidOperationException("Session recovery has not been configured for this peer.");
    }
}
