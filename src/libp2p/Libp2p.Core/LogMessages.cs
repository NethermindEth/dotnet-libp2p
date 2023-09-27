// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Core;

internal static partial class LogMessages
{
    [LoggerMessage(
        Message = "Read chunk {bytes} bytes",
        Level = LogLevel.Trace)]
    internal static partial void ReadChunk(
        this ILogger logger,
        long bytes);

    [LoggerMessage(
        Message = "Read enough {bytes} bytes",
        Level = LogLevel.Trace)]
    internal static partial void ReadEnough(
        this ILogger logger,
        long bytes);

    [LoggerMessage(
        Message = "Write {bytes} bytes",
        Level = LogLevel.Trace)]
    internal static partial void WriteBytes(
        this ILogger logger,
        long bytes);

    [LoggerMessage(
        Message = "Dial {channel} on protocol {protocol} with sub-protocols {subProtocols}",
        Level = LogLevel.Debug)]
    internal static partial void DialStarted(
        this ILogger logger,
        string channel,
        string protocol,
        IEnumerable<string> subProtocols);

    [LoggerMessage(
        Message = "Listen {channel} on protocol {protocol} with sub-protocols {subProtocols}",
        Level = LogLevel.Debug)]
    internal static partial void ListenStarted(
        this ILogger logger,
        string channel,
        string protocol,
        IEnumerable<string> subProtocols);

    [LoggerMessage(
        Message = "Dial and bind {channel} on protocol {protocol} with sub-protocols {subProtocols}",
        Level = LogLevel.Debug)]
    internal static partial void DialAndBindStarted(
        this ILogger logger,
        string channel,
        string protocol,
        IEnumerable<string> subProtocols);

    [LoggerMessage(
        Message = "Listen and bind {channel} on protocol {protocol} with sub-protocols {subProtocols}",
        Level = LogLevel.Debug)]
    internal static partial void ListenAndBindStarted(
        this ILogger logger,
        string channel,
        string protocol,
        IEnumerable<string> subProtocols);

    [LoggerMessage(
        Message = "Create channel {chan}",
        Level = LogLevel.Debug)]
    internal static partial void ChannelCreated(
        this ILogger logger,
        string chan);

    [LoggerMessage(
        Message = "Dial error {protocol} via {channel}: {errorMessage}",
        Level = LogLevel.Error,
        SkipEnabledCheck = true)]
    internal static partial void DialFailed(
        this ILogger logger,
        string channel,
        string protocol,
        Exception? exception,
        string errorMessage);

    [LoggerMessage(
        Message = "Listen error {protocol} via {channel}: {errorMessage}",
        Level = LogLevel.Error,
        SkipEnabledCheck = true)]
    internal static partial void ListenFailed(
        this ILogger logger,
        string channel,
        string protocol,
        Exception? exception,
        string errorMessage);

    [LoggerMessage(
        Message = "Dial and bind error {protocol} via {channel}: {errorMessage}",
        Level = LogLevel.Error,
        SkipEnabledCheck = true)]
    internal static partial void DialAndBindFailed(
        this ILogger logger,
        string channel,
        string protocol,
        Exception? exception,
        string errorMessage);

    [LoggerMessage(
        Message = "Listen and bind error {protocol} via {channel}: {errorMessage}",
        Level = LogLevel.Error,
        SkipEnabledCheck = true)]
    internal static partial void ListenAndBindFailed(
        this ILogger logger,
        string channel,
        string protocol,
        Exception? exception,
        string errorMessage);
}
