// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht;

public interface IKademliaNodeAdapter<TNode>
{
    string GetNodeId(TNode node);
    IEnumerable<string> GetMultiAddresses(TNode node);
}
