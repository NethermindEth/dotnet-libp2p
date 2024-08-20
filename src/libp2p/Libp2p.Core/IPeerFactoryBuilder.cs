// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IPeerFactoryBuilder
{
    IPeerFactoryBuilder AddAppLayerProtocol<TProtocol>(TProtocol? instance = default, bool isExposed = true) where TProtocol : IProtocol;
    IPeerFactory Build();
    IEnumerable<IProtocol> AppLayerProtocols { get; }
}
