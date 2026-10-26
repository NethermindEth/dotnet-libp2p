// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Core;

internal static partial class LogMessages
{
    private const int EventId = 101_000;

    [LoggerMessage(
        EventId = EventId + 1,
        EventName = nameof(ReadChunk),
        Message = "Read chunk {bytes} bytes",
        Level = LogLevel.Trace)]
    internal static partial void ReadChunk(
        this ILogger logger,
        long bytes);

    [LoggerMessage(
        EventId = EventId + 2,
        EventName = nameof(ReadEnough),
        Message = "Read enough {bytes} bytes",
        Level = LogLevel.Trace)]
    internal static partial void ReadEnough(
        this ILogger logger,
        long bytes);

    [LoggerMessage(
        EventId = EventId + 3,
        EventName = nameof(WriteBytes),
        Message = "Write {bytes} bytes",
        Level = LogLevel.Trace)]
    internal static partial void WriteBytes(
        this ILogger logger,
        long bytes);

    [LoggerMessage(
        EventId = EventId + 4,
        EventName = nameof(DialStarted),
        Message = "Dial {channel} on protocol {protocol} with sub-protocols {subProtocols}",
        Level = LogLevel.Debug)]
    internal static partial void DialStarted(
        this ILogger logger,
        string channel,
        string protocol,
        IEnumerable<string> subProtocols);

    [LoggerMessage(
        EventId = EventId + 5,
        EventName = nameof(ListenStarted),
        Message = "Listen {channel} on protocol {protocol} with sub-protocols {subProtocols}",
        Level = LogLevel.Debug)]
    internal static partial void ListenStarted(
        this ILogger logger,
        string channel,
        string protocol,
        IEnumerable<string> subProtocols);

    [LoggerMessage(
        EventId = EventId + 6,
        EventName = nameof(DialAndBindStarted),
        Message = "Dial and bind {channel} on protocol {protocol} with sub-protocols {subProtocols}",
        Level = LogLevel.Debug)]
    internal static partial void DialAndBindStarted(
        this ILogger logger,
        string channel,
        string protocol,
        IEnumerable<string> subProtocols);

    [LoggerMessage(
        EventId = EventId + 7,
        EventName = nameof(ListenAndBindStarted),
        Message = "Listen and bind {channel} on protocol {protocol} with sub-protocols {subProtocols}",
        Level = LogLevel.Debug)]
    internal static partial void ListenAndBindStarted(
        this ILogger logger,
        string channel,
        string protocol,
        IEnumerable<string> subProtocols);

    [LoggerMessage(
        EventId = EventId + 8,
        EventName = nameof(ChannelCreated),
        Message = "Create channel {chan}",
        Level = LogLevel.Debug)]
    internal static partial void ChannelCreated(
        this ILogger logger,
        string chan);

    [LoggerMessage(
        EventId = EventId + 9,
        EventName = nameof(DialFailed),
        Message = "Dial error {protocol}: {errorMessage}",
        Level = LogLevel.Error,
        SkipEnabledCheck = true)]
    internal static partial void DialFailed(
        this ILogger logger,
        string protocol,
        Exception? exception,
        string errorMessage);

    [LoggerMessage(
        EventId = EventId + 10,
        EventName = nameof(ListenFailed),
        Message = "Listen error {protocol}: {errorMessage}",
        Level = LogLevel.Error,
        SkipEnabledCheck = true)]
    internal static partial void ListenFailed(
        this ILogger logger,
        string protocol,
        Exception? exception,
        string errorMessage);

    [LoggerMessage(
        EventId = EventId + 11,
        EventName = nameof(DialAndBindFailed),
        Message = "Dial and bind error {protocol}: {errorMessage}",
        Level = LogLevel.Error,
        SkipEnabledCheck = true)]
    internal static partial void DialAndBindFailed(
        this ILogger logger,
        string protocol,
        Exception? exception,
        string errorMessage);

    [LoggerMessage(
        EventId = EventId + 12,
        EventName = nameof(ListenAndBindFailed),
        Message = "Listen and bind error {protocol}: {errorMessage}",
        Level = LogLevel.Error,
        SkipEnabledCheck = true)]
    internal static partial void ListenAndBindFailed(
        this ILogger logger,
        string protocol,
        Exception? exception,
        string errorMessage);
}
