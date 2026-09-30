// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;

namespace Nethermind.Libp2p.Protocols.Ping;

internal static partial class LogMessages
{
    private const int EventId = 200_000;

    [LoggerMessage(
        EventId = EventId + 1,
        EventName = nameof(ReadingPong),
        Message = "Reading pong {remotePeer}",
        Level = LogLevel.Trace)]
    internal static partial void ReadingPong(
        this ILogger logger,
        Multiaddress remotePeer);

    [LoggerMessage(
        EventId = EventId + 2,
        EventName = nameof(VerifyingPong),
        Message = "Verifying pong {remotePeer}",
        Level = LogLevel.Trace)]
    internal static partial void VerifyingPong(
        this ILogger logger,
        Multiaddress remotePeer);

    [LoggerMessage(
        EventId = EventId + 3,
        EventName = nameof(ReadingPing),
        Message = "Reading ping {remotePeer}",
        Level = LogLevel.Trace)]
    internal static partial void ReadingPing(
        this ILogger logger,
        Multiaddress remotePeer);

    [LoggerMessage(
        EventId = EventId + 4,
        EventName = nameof(ReturningPong),
        Message = "Returning pong {remotePeer}",
        Level = LogLevel.Trace)]
    internal static partial void ReturningPong(
        this ILogger logger,
        Multiaddress remotePeer);

    [LoggerMessage(
        EventId = EventId + 5,
        EventName = nameof(LogPing),
        Message = "Ping {remotePeer}",
        Level = LogLevel.Debug)]
    internal static partial void LogPing(
        this ILogger logger,
        Multiaddress remotePeer);

    [LoggerMessage(
        EventId = EventId + 6,
        EventName = nameof(LogPinged),
        Message = "Pinged {remotePeer}",
        Level = LogLevel.Debug)]
    internal static partial void LogPinged(
        this ILogger logger,
        Multiaddress remotePeer);

    [LoggerMessage(
        EventId = EventId + 7,
        EventName = nameof(PingListenStarted),
        Message = "Ping listen started from {remotePeer}",
        Level = LogLevel.Debug)]
    internal static partial void PingListenStarted(
        this ILogger logger,
        Multiaddress remotePeer);

    [LoggerMessage(
        EventId = EventId + 8,
        EventName = nameof(PingFinished),
        Message = "Ping finished {remotePeer}",
        Level = LogLevel.Debug)]
    internal static partial void PingFinished(
        this ILogger logger,
        Multiaddress remotePeer);

    [LoggerMessage(
        EventId = EventId + 9,
        EventName = nameof(PingFailed),
        Message = "Wrong response to ping from {remotePeer}",
        Level = LogLevel.Warning)]
    internal static partial void PingFailed(
        this ILogger logger,
        Multiaddress remotePeer);
}
