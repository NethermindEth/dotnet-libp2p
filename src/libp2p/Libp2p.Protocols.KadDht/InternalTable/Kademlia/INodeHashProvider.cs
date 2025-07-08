// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Libp2p.Protocols.KadDht.InternalTable.Crypto;


using Libp2p.Protocols.KadDht.InternalTable.Crypto;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia
{
    /// <summary>
    /// Just a convenient interface with only one generic parameter.
    /// </summary>
    /// <typeparam name="TNode"></typeparam>
    public interface INodeHashProvider<in TNode>
    {
        ValueHash256 GetHash(TNode node);
    }
}

