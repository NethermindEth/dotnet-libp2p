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
        ArgumentNullException.ThrowIfNull(handler);
        return builder.AddRequestResponseProtocol<TRequest, TResponse>(readTimeout,
            (request, context, _) => handler(request, context), protocolId,
            isExposed, expectsResponse, maxMessageSize, timeProvider);
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
        RequestResponseProtocol<TRequest, TResponse>.ValidateReadOptions(readTimeout, maxMessageSize);
        ArgumentNullException.ThrowIfNull(protocolId);
        ArgumentNullException.ThrowIfNull(cancellationHandler);

        RequestResponseProtocol<TRequest, TResponse> CreateProtocol(IServiceProvider services)
            => new(readTimeout, cancellationHandler, protocolId, services.GetService<ILoggerFactory>(),
                expectsResponse, maxMessageSize, timeProvider ?? services.GetService<TimeProvider>());

        if (builder is IProtocolFactoryBuilder factories)
        {
            factories.AddProtocolFactory(services => new ProtocolRef(CreateProtocol(services), isExposed));
            return builder;
        }
        return builder.AddProtocol(CreateProtocol(builder.ServiceProvider), isExposed);
    }
}
