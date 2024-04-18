// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Libp2p.Core;
public static class UnwarpResultExtensions
{
    public static async ValueTask OrThrow(this ValueTask<IOResult> self)
    {
        if (self.IsCompleted && self.Result != IOResult.Ok)
        {
            throw new Exception();
        }
        var result = await self.AsTask();
        if (result != IOResult.Ok)
        {
            throw new Exception($"Unable to read, error: {result}");
        }
    }
    public static async ValueTask<ReadOnlySequence<byte>> OrThrow(this ValueTask<ReadResult> self)
    {
        if (self.IsCompleted && self.Result.Result != IOResult.Ok)
        {
            throw new Exception();
        }
        var result = await self.AsTask();
        if (result.Result != IOResult.Ok)
        {
            throw new Exception($"Unable to read, error: {result}");
        }
        else
        {
            return result.Data;
        }
    }
}
