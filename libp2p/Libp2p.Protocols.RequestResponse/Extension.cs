// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols;

public static class RequestResponseExtensions
{
    public static IPeerFactoryBuilder AddRequestResponseProtocol<TRequest, TResponse>(
        this IPeerFactoryBuilder builder,
        string protocolId,
        Func<TRequest, ISessionContext, Task<TResponse>> handler,
        bool isExposed = true,
        Func<TRequest, bool>? expectsResponse = null,
        int maxMessageSize = int.MaxValue)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage<TResponse>, new()
        => builder.AddRequestResponseProtocol(RequestResponseProtocol<TRequest, TResponse>.DefaultReadTimeout,
            protocolId, handler, isExposed, expectsResponse, maxMessageSize);

    public static IPeerFactoryBuilder AddRequestResponseProtocol<TRequest, TResponse>(
        this IPeerFactoryBuilder builder,
        TimeSpan readTimeout,
        string protocolId,
        Func<TRequest, ISessionContext, Task<TResponse>> handler,
        bool isExposed = true,
        Func<TRequest, bool>? expectsResponse = null,
        int maxMessageSize = int.MaxValue,
        TimeProvider? timeProvider = null)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage<TResponse>, new()
    {
        var protocol = new RequestResponseProtocol<TRequest, TResponse>(
            readTimeout, protocolId,
            handler,
            builder.ServiceProvider.GetService<ILoggerFactory>(),
            expectsResponse,
            maxMessageSize, timeProvider ?? builder.ServiceProvider.GetService<TimeProvider>());

        return builder.AddProtocol(protocol, isExposed);
    }

    public static IPeerFactoryBuilder AddRequestResponseProtocol<TRequest, TResponse>(
        this IPeerFactoryBuilder builder,
        Func<TRequest, ISessionContext, CancellationToken, Task<TResponse>> cancellationHandler,
        string protocolId,
        bool isExposed = true,
        Func<TRequest, bool>? expectsResponse = null,
        int maxMessageSize = int.MaxValue)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage<TResponse>, new()
        => builder.AddRequestResponseProtocol(RequestResponseProtocol<TRequest, TResponse>.DefaultReadTimeout,
            cancellationHandler, protocolId, isExposed, expectsResponse, maxMessageSize);

    public static IPeerFactoryBuilder AddRequestResponseProtocol<TRequest, TResponse>(
        this IPeerFactoryBuilder builder,
        TimeSpan readTimeout,
        Func<TRequest, ISessionContext, CancellationToken, Task<TResponse>> cancellationHandler,
        string protocolId,
        bool isExposed = true,
        Func<TRequest, bool>? expectsResponse = null,
        int maxMessageSize = int.MaxValue,
        TimeProvider? timeProvider = null)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage<TResponse>, new()
    {
        var protocol = new RequestResponseProtocol<TRequest, TResponse>(
            readTimeout, cancellationHandler, protocolId, builder.ServiceProvider.GetService<ILoggerFactory>(), expectsResponse, maxMessageSize,
            timeProvider ?? builder.ServiceProvider.GetService<TimeProvider>());
        return builder.AddProtocol(protocol, isExposed);
    }
}
