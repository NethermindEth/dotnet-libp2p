// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface ILibp2pPeerFactoryBuilder : IPeerFactoryBuilder
{
    public ILibp2pPeerFactoryBuilder WithPlaintextEnforced();
    public ILibp2pPeerFactoryBuilder WithPubsub();
    public ILibp2pPeerFactoryBuilder WithRelay();
}
