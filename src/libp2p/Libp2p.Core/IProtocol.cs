// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public interface IProtocol
{    
    string Id { get; }
}

public interface ITransportProtocol : IProtocol
{
    Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token);
    Task DialAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token);
}

public interface IConnectionProtocol : IProtocol
{
    Task ListenAsync(IChannel downChannel, IConnectionContext context);
    Task DialAsync(IChannel downChannel, IConnectionContext context);
}

public interface ISessionProtocol : IProtocol
{
    Task ListenAsync(IChannel downChannel, ISessionContext context);
    Task DialAsync(IChannel downChannel, ISessionContext context);
}
