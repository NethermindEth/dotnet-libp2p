// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht;

public sealed class SessionOptions
{
    public IReadOnlyList<string> BootstrapMultiAddresses { get; init; } = Array.Empty<string>();
    public int? KSize { get; init; }
    public TimeSpan? RefreshInterval { get; init; }
    public bool EnableMetrics { get; init; } = true;
}
