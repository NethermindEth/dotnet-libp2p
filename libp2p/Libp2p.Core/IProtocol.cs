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
    static bool IsAddressMatch(IProtocol proto, Multiaddress addr) => (bool)proto.GetType()
          .GetMethod(nameof(IsAddressMatch))!.Invoke(null, [addr])!;
    static Multiaddress[] GetDefaultAddresses(IProtocol proto, PeerId peerId) => (Multiaddress[])proto.GetType()
          .GetMethod(nameof(GetDefaultAddresses))!.Invoke(null, [peerId])!;


    static abstract Multiaddress[] GetDefaultAddresses(PeerId peerId);
    static abstract bool IsAddressMatch(Multiaddress addr);

    Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token);
    Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token);
}

public interface IConnectionProtocol : IProtocol
{
    Task ListenAsync(IChannel downChannel, IConnectionContext context);
    Task DialAsync(IChannel downChannel, IConnectionContext context);
}


public interface ISessionListenerProtocol : IProtocol
{
    Task ListenAsync(IChannel downChannel, ISessionContext context);
}

public interface ISessionProtocol : ISessionListenerProtocol
{
    Task DialAsync(IChannel downChannel, ISessionContext context);
}

public interface ISessionProtocol<TRequest, TResponse> : ISessionListenerProtocol
{
    Task<TResponse> DialAsync(IChannel downChannel, ISessionContext context, TRequest request);
}
