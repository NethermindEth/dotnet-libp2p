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
        bool isExposed = true)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage<TResponse>, new()
    {
        var protocol = new RequestResponseProtocol<TRequest, TResponse>(
            protocolId,
            handler,
            builder.ServiceProvider.GetService<ILoggerFactory>());

        return builder.AddProtocol(protocol, isExposed);
    }
}
