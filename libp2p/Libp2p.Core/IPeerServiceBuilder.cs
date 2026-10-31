// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

/// <summary>Optional builder capability for services owned by each local peer, outside the wire protocol graph.</summary>
public interface IPeerServiceBuilder
{
    void AddPeerService<TService>(Func<ILocalPeer, IServiceProvider, TService> factory, bool ownsInstance = true) where TService : class;
}

/// <summary>Optional capability for protocol registrations that depend on the final services.</summary>
public interface IProtocolFactoryBuilder
{
    void AddProtocolFactory(Func<IServiceProvider, ProtocolRef> factory);
}

/// <summary>Optional capability for retrieving peer-owned services.</summary>
public interface IPeerServices
{
    TService? GetPeerService<TService>() where TService : class;
}

internal sealed record PeerServiceRegistration(Func<ILocalPeer, object> Factory, bool OwnsInstance);

internal static class PeerServiceRegistry
{
    private static readonly ConditionalWeakTable<IProtocolStackSettings, Dictionary<Type, PeerServiceRegistration>> Registrations = new();

    internal static void Configure(IProtocolStackSettings settings, Dictionary<Type, PeerServiceRegistration> factories)
    {
        Registrations.Remove(settings);
        Registrations.Add(settings, new(factories));
    }

    internal static PeerServiceRegistration? Get(IProtocolStackSettings settings, Type type) =>
        Registrations.TryGetValue(settings, out var factories) && factories.TryGetValue(type, out var registration) ? registration : null;
}
