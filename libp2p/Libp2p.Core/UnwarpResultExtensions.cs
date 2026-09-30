// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Exceptions;
using System.Buffers;

namespace Nethermind.Libp2p.Core;

public static class UnwrapResultExtensions
{
    public static async ValueTask OrThrow(this ValueTask<IOResult> self)
    {
        if (self.IsCompleted && self.Result != IOResult.Ok)
        {
            throw new ChannelClosedException(self.Result);
        }

        IOResult result = await self.AsTask();

        if (result != IOResult.Ok)
        {
            throw new ChannelClosedException(result);
        }
    }
    public static async ValueTask<ReadOnlySequence<byte>> OrThrow(this ValueTask<ReadResult> self)
    {
        if (self.IsCompleted && self.Result.Result != IOResult.Ok)
        {
            throw new ChannelClosedException(self.Result.Result);
        }

        ReadResult result = await self.AsTask();

        if (result.Result != IOResult.Ok)
        {
            throw new ChannelClosedException(result.Result);
        }
        else
        {
            return result.Data;
        }
    }
}
