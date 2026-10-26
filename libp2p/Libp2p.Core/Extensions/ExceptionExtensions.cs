// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Extensions;

internal static class ExceptionExtensions
{
    public static string GetErrorMessage(this Exception? exception)
        => exception?.Message ?? "unknown";
}
