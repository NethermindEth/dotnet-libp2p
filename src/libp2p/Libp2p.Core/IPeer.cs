// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core;

public interface IPeer
{
    Identity Identity { get; }

    Task<ISession> DialAsync(Multiaddress addr, CancellationToken token = default);
    Task<ISession> DialAsync(Multiaddress[] samePeerAddrs, CancellationToken token = default);

    /// <summary>
    /// Find existing session or dial a peer if found in peer store
    /// </summary>
    Task<ISession> DialAsync(PeerId peerId, CancellationToken token = default);

    Task StartListenAsync(Multiaddress[] addrs, CancellationToken token = default);

    Task DisconnectAsync();

    ObservableCollection<Multiaddress> ListenAddresses { get; }

    event Connected? OnConnected;
}

public delegate Task Connected(ISession newSession);

public interface ISession
{
    Multiaddress RemoteAddress { get; }
    Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol;
    Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>;
    Task DisconnectAsync();
}
