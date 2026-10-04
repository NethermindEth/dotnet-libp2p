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
        Func<TRequest, bool>? expectsResponse = null)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage<TResponse>, new()
    {
        return builder.AddRequestResponseProtocol(
            protocolId,
            handler,
            int.MaxValue,
            isExposed,
            expectsResponse);
    }

    public static IPeerFactoryBuilder AddRequestResponseProtocol<TRequest, TResponse>(
        this IPeerFactoryBuilder builder,
        string protocolId,
        Func<TRequest, ISessionContext, Task<TResponse>> handler,
        int maxMessageSize,
        bool isExposed = true,
        Func<TRequest, bool>? expectsResponse = null)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage<TResponse>, new()
    {
        var protocol = new RequestResponseProtocol<TRequest, TResponse>(
            protocolId,
            handler,
            maxMessageSize,
            builder.ServiceProvider.GetService<ILoggerFactory>(),
            expectsResponse);

        return builder.AddProtocol(protocol, isExposed);
    }
}
