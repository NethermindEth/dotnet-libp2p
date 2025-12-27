// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public interface ISession
{
    Multiaddress RemoteAddress { get; }
    Activity? Activity { get; }
    /// <summary>
    /// Initiate session communication for symmetric protocol without needing a request object, for asymmetric protocol use DialAsync&lt;ISessionProtocol&lt;'req, 'res&gt;, 'req, 'res&gt;('req request) instead
    /// </summary>
    /// <typeparam name="TProtocol"></typeparam>
    /// <param name="token"></param>
    /// <returns></returns>
    Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol;
    /// <summary>
    /// Initiate session communication
    /// </summary>
    /// <typeparam name="TProtocol"></typeparam>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    /// <param name="request"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>;
    Task DisconnectAsync();
}
