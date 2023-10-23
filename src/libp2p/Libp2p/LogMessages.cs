// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Stack;

internal static partial class LogMessages
{
    private const int EventId = 100_000;

    [LoggerMessage(
        EventId = EventId + 1,
        EventName = nameof(LogPickedProtocol),
        Message = "{protocol} has been picked to {action}",
        Level = LogLevel.Debug)]
    internal static partial void LogPickedProtocol(
        this ILogger logger,
        string protocol,
        string action);
}
