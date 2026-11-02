// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Channels;

public sealed class ChannelClosedException(IOResult result) : IOException($"Channel operation failed: {result}.")
{
    public IOResult Result { get; } = result;
}
