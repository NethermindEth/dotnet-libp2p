// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Libp2p.Protocols.KadDht;

public interface ISessionManager
{
    Task BootstrapAsync(CancellationToken ct);
    Task RunAsync(CancellationToken ct);
    Task<TNode[]> DiscoverAsync<TNode>(object targetKey, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
