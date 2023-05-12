// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IPeerFactoryBuilder
{
    IPeerFactoryBuilder AddAppLayerProtocol<TProtocol>(TProtocol? instance = default) where TProtocol : IProtocol;
    IPeerFactory Build();
    IEnumerable<IProtocol> AppLayerProtocols { get; }
}

public interface ILibp2pPeerFactoryBuilder : IPeerFactoryBuilder
{
    public bool EnforcePlaintext { set; }
}
