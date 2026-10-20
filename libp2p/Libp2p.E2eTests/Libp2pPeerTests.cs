// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class Libp2pPeerTests
{
    [Test]
    public void StandardPeerIsPublic()
    {
        Type peerType = typeof(Libp2pPeerFactory).Assembly.GetType("Nethermind.Libp2p.Libp2pPeer", throwOnError: true)!;
        Assert.That(peerType.IsVisible, Is.True, "Consumers must be able to derive from the standard peer.");
    }
}
