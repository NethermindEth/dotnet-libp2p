// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Ping;

/// <summary>
/// 200_000..201_099 Trace
/// 200_100..201_199 Debug
/// 200_100..202_299 Information
/// 200_100..203_399 Warning
/// 200_100..204_499 Error
/// 200_100..205_599 Critical
/// </summary>
internal static partial class LogMessages
{
    [LoggerMessage(
        EventId = 200001,
        EventName = nameof(ReadingPong),
        Message = "Reading pong",
        Level = LogLevel.Trace)]
    internal static partial void ReadingPong(
        this ILogger logger);

    [LoggerMessage(
        EventId = 200002,
        EventName = nameof(VerifyingPong),
        Message = "Verifying pong",
        Level = LogLevel.Trace)]
    internal static partial void VerifyingPong(
        this ILogger logger);

    [LoggerMessage(
        EventId = 200003,
        EventName = nameof(ReadingPing),
        Message = "Reading ping",
        Level = LogLevel.Trace)]
    internal static partial void ReadingPing(
        this ILogger logger);

    [LoggerMessage(
        EventId = 200004,
        EventName = nameof(ReturningPong),
        Message = "Returning pong",
        Level = LogLevel.Trace)]
    internal static partial void ReturningPong(
        this ILogger logger);

    [LoggerMessage(
        EventId = 200101,
        EventName = nameof(LogPing),
        Message = "Ping {remotePeer}",
        Level = LogLevel.Debug)]
    internal static partial void LogPing(
        this ILogger logger,
        Multiaddr remotePeer);

    [LoggerMessage(
        EventId = 200102,
        EventName = nameof(LogPinged),
        Message = "Pinged",
        Level = LogLevel.Debug)]
    internal static partial void LogPinged(
        this ILogger logger);

    [LoggerMessage(
        EventId = 200103,
        EventName = nameof(PingListenStarted),
        Message = "Ping listen started from {remotePeer}",
        Level = LogLevel.Debug)]
    internal static partial void PingListenStarted(
        this ILogger logger,
        Multiaddr remotePeer);

    [LoggerMessage(
        EventId = 200104,
        EventName = nameof(PingFinished),
        Message = "Ping finished",
        Level = LogLevel.Debug)]
    internal static partial void PingFinished(
        this ILogger logger);

    [LoggerMessage(
        EventId = 200301,
        EventName = nameof(PingFailed),
        Message = "Wrong response to ping from {remotePeer}",
        Level = LogLevel.Warning)]
    internal static partial void PingFailed(
        this ILogger logger,
        Multiaddr remotePeer);
}
