// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Channels;

public static class UnwrapResultExtensions
{
    public static async ValueTask OrThrow(this ValueTask<IOResult> operation)
    {
        IOResult result = await operation.ConfigureAwait(false);
        if (result != IOResult.Ok) throw new ChannelClosedException(result);
    }

    public static async ValueTask<ReadResult> OrThrow(this ValueTask<ReadResult> operation)
    {
        ReadResult result = await operation.ConfigureAwait(false);
        if (result.Result == IOResult.Ok) return result;
        result.Dispose();
        throw new ChannelClosedException(result.Result);
    }
}
