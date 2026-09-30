// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class MDnsDiscoveryLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task DisposeAsync_stops_discovery_without_caller_cancellation()
    {
        MDnsDiscoveryProtocol discovery = new(new PeerStore());
        Multiaddress localAddr = $"/ip4/127.0.0.1/tcp/4001/p2p/{TestPeers.PeerId(0)}";

        await discovery.StartDiscoveryAsync([localAddr]);

        await discovery.DisposeAsync().AsTask().WaitAsync(Timeout);
        await discovery.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.ThrowsAsync<ObjectDisposedException>(() => discovery.StartDiscoveryAsync([localAddr]));
    }
}
