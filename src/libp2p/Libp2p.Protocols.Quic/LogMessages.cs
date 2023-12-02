// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Quic;

internal static partial class LogMessages
{
    private const int EventId = 200_001;

    [LoggerMessage(
        EventId = EventId + 1,
        EventName = nameof(ReadyToHandleConnections),
        Message = "Ready to handle connections",
        Level = LogLevel.Debug)]
    internal static partial void ReadyToHandleConnections(
        this ILogger logger);

    [LoggerMessage(
        EventId = EventId + 2,
        EventName = nameof(Connected),
        Message = "Connected {localEndPoint} --> {remoteEndPoint}",
        Level = LogLevel.Debug)]
    internal static partial void Connected(
        this ILogger logger,
        IPEndPoint localEndPoint,
        IPEndPoint remoteEndPoint);

    [LoggerMessage(
        EventId = EventId + 3,
        EventName = nameof(SocketException),
        Message = "Disconnected due to a socket exception: {errorMessage}",
        Level = LogLevel.Information)]
    internal static partial void SocketException(
        this ILogger logger,
        Exception exception,
        string errorMessage);
}
