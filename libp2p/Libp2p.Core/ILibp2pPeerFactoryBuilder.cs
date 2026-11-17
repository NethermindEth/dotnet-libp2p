// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core;

public interface ILibp2pPeerFactoryBuilder : IPeerFactoryBuilder
{
    /// <summary>
    /// Provides access to the underlying service collection for advanced protocol integration.
    /// This enables protocols like KadDHT to register their services properly.
    /// </summary>
    IServiceCollection Services { get; }

    public ILibp2pPeerFactoryBuilder WithPlaintextEnforced();
    public ILibp2pPeerFactoryBuilder WithPubsub();
    public ILibp2pPeerFactoryBuilder WithRelay();
    public ILibp2pPeerFactoryBuilder WithQuic();
}
