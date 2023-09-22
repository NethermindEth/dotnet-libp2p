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
        Message = "{method} {chan} on protocol {protocols} with sub-protocols {subProtocols}",
        Level = LogLevel.Debug)]
    internal static partial void LogAction(
        this ILogger logger,
        string method,
        string chan,
        string protocol,
        IEnumerable<string> subProtocols);

    [LoggerMessage(
        Message = "Create chan {chan}",
        Level = LogLevel.Debug)]
    internal static partial void ChanCreated(
        this ILogger logger,
        string chan);

    [LoggerMessage(
        Message = "{method} error {protocol} via {chan}: {errorMessage}",
        Level = LogLevel.Error,
        SkipEnabledCheck = true)]
    internal static partial void LogCompletedUnsuccessfully(
        this ILogger logger,
        string method,
        string chan,
        string protocol,
        Exception? exception,
        string errorMessage);
}
