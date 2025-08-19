// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols;

public static class RequestResponseExtensions
{
    public static IPeerFactoryBuilder AddRequestResponseProtocol<TRequest, TResponse>(
        this IPeerFactoryBuilder builder,
        string protocolId,
        Func<TRequest, ISessionContext, Task<TResponse>> handler,
        ILoggerFactory? loggerFactory = null,
        bool isExposed = true)
        where TRequest : class, IMessage<TRequest>, new()
        where TResponse : class, IMessage<TResponse>, new()
    {
        var protocol = new RequestResponseProtocol<TRequest, TResponse>(
            protocolId,
            handler,
            loggerFactory);

        return builder.AddAppLayerProtocol(protocol, isExposed);
    }
}
