// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public interface ISession
{
    /// <summary>The authenticated remote peer's identity.</summary>
    /// <remarks>The default implementation reads the destination peer id from <see cref="RemoteAddress"/>.</remarks>
    /// <exception cref="InvalidOperationException">The remote address does not contain a destination peer id.</exception>
    PeerId RemotePeerId => RemoteAddress.GetPeerId()
        ?? throw new InvalidOperationException("The session's remote address does not contain a destination peer id.");
    Multiaddress RemoteAddress { get; }
    Activity? Activity { get; }

    /// <summary>Whether this session has successfully completed initialization.</summary>
    /// <remarks>
    /// The default implementation returns true for sessions without a separate initialization stage.
    /// Check <see cref="Closed"/> separately for closure.
    /// </remarks>
    bool IsInitialized => true;

    /// <summary>Canceled when this session starts closing, including transport loss, if lifetime notifications are supported.</summary>
    /// <remarks>
    /// The default implementation returns <see cref="CancellationToken.None"/> for sessions without lifetime notifications.
    /// Check <see cref="CancellationToken.CanBeCanceled"/> before relying on this token to observe closure.
    /// </remarks>
    CancellationToken Closed => CancellationToken.None;

    /// <summary>The reason this session started closing. Read it after <see cref="Closed"/> is canceled.</summary>
    /// <remarks>
    /// Sessions that report a reason must set it before canceling <see cref="Closed"/> and keep it stable afterward.
    /// The default implementation reports <see cref="SessionCloseReason.Unknown"/>.
    /// </remarks>
    SessionCloseReason CloseReason => SessionCloseReason.Unknown;

    /// <summary>
    /// Dials a session protocol that does not require a request payload.
    /// </summary>
    /// <typeparam name="TProtocol">The application protocol to negotiate over this session.</typeparam>
    /// <param name="token">Cancels the queued request or signals cancellation to the running stream invocation.</param>
    /// <returns>A task that completes when the dial request has been handled.</returns>
    Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol;

    /// <summary>Dials a session protocol without a request payload and returns its response.</summary>
    /// <param name="token">Cancels the queued request or signals cancellation to the running stream invocation.</param>
    /// <remarks>The default implementation does not support typed responses. Implementations must override this method to return protocol results.</remarks>
    /// <exception cref="NotSupportedException">The session implementation does not support typed responses.</exception>
    Task<TResponse> DialAsync<TProtocol, TResponse>(CancellationToken token = default) where TProtocol : ISessionProtocol<TResponse>
        => Task.FromException<TResponse>(new NotSupportedException("This session does not support typed protocol responses."));

    /// <summary>
    /// Dials a session protocol with a request payload and returns the protocol response.
    /// </summary>
    /// <typeparam name="TProtocol">The application protocol to negotiate over this session.</typeparam>
    /// <typeparam name="TRequest">The request type accepted by the protocol.</typeparam>
    /// <typeparam name="TResponse">The response type returned by the protocol.</typeparam>
    /// <param name="request">Request payload passed to the protocol.</param>
    /// <param name="token">Cancels the queued request or signals cancellation to the running stream invocation.</param>
    /// <returns>The response produced by the selected protocol.</returns>
    Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>;

    /// <summary>Dials the selected protocol instance. Older session implementations may not support instance dialing.</summary>
    /// <param name="protocol">The protocol instance to negotiate.</param>
    /// <param name="token">Cancels the queued request or signals cancellation to the running stream invocation.</param>
    /// <exception cref="NotSupportedException">The session does not implement instance dialing.</exception>
    Task DialAsync(ISessionProtocol protocol, CancellationToken token = default)
        => Task.FromException(new NotSupportedException("This session does not support dialing protocol instances."));

    /// <summary>Dials the selected protocol instance with a request and returns its response.</summary>
    /// <param name="protocol">The protocol instance to negotiate.</param>
    /// <param name="request">The payload passed to the protocol.</param>
    /// <param name="token">Cancels the queued request or signals cancellation to the running stream invocation.</param>
    /// <exception cref="NotSupportedException">The session does not implement instance dialing.</exception>
    Task<TResponse> DialAsync<TRequest, TResponse>(ISessionProtocol<TRequest, TResponse> protocol, TRequest request, CancellationToken token = default)
        => Task.FromException<TResponse>(new NotSupportedException("This session does not support dialing protocol instances."));

    /// <summary>Requests disconnection of the session.</summary>
    /// <remarks>A native session reports <see cref="SessionCloseReason.LocalDisconnect"/> when this request starts closure.</remarks>
    Task DisconnectAsync();
}
