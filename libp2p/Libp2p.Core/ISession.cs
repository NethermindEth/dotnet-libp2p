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
    /// Dials a session protocol that does not require a request payload.
    /// </summary>
    /// <typeparam name="TProtocol">The application protocol to negotiate over this session.</typeparam>
    /// <param name="token">Cancellation token used while queueing the dial request.</param>
    /// <returns>A task that completes when the dial request has been handled.</returns>
    Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol;

    /// <summary>
    /// Dials a session protocol with a request payload and returns the protocol response.
    /// </summary>
    /// <typeparam name="TProtocol">The application protocol to negotiate over this session.</typeparam>
    /// <typeparam name="TRequest">The request type accepted by the protocol.</typeparam>
    /// <typeparam name="TResponse">The response type returned by the protocol.</typeparam>
    /// <param name="request">Request payload passed to the protocol.</param>
    /// <param name="token">Cancellation token used while queueing the dial request.</param>
    /// <returns>The response produced by the selected protocol.</returns>
    Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>;

    /// <summary>Dials the selected protocol instance. Older session implementations may not support instance dialing.</summary>
    /// <exception cref="NotSupportedException">The session does not implement instance dialing.</exception>
    Task DialAsync(ISessionProtocol protocol, CancellationToken token = default)
        => Task.FromException(new NotSupportedException("This session does not support dialing protocol instances."));

    Task DisconnectAsync();
}
