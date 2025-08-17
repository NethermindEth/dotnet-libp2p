// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IPeerFactoryBuilder
{
    /// <summary>
    /// Adds application layer protocol to the stack. See <see href="https://github.com/NethermindEth/dotnet-libp2p/blob/main/docs/README.md#make-a-protocol-for-your-application">Defining application layer protocol</see>.
    /// </summary>
    /// <typeparam name="TProtocol">Protocol type</typeparam>
    /// <param name="instance">Instance of the protocol can be passed if manual creation is preferred over automatic creation via dependency injection.</param>
    /// <param name="isExposed">Whether information about protocol support is shared during the handshake.</param>
    /// <returns>The same builder for chaining calls</returns>
    IPeerFactoryBuilder AddProtocol<TProtocol>(TProtocol? instance = default, bool isExposed = true) where TProtocol : IProtocol;
    IPeerFactory Build();
}
