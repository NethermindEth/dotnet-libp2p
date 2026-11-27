// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Discovery;

namespace Nethermind.Libp2p.Core.TestsBase;

public static class PeerStoreTestExtensions
{
    public static PeerMetadata ReadPeerInfo(this IPeerStore store, PeerId peerId) =>
        store.TryGetPeerInfo(peerId, out PeerMetadata? metadata)
            ? metadata
            : throw new InvalidOperationException($"No metadata was stored for peer {peerId}.");
}
