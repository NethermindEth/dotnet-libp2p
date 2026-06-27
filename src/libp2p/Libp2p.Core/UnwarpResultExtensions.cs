// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Libp2p.Core;

public static class UnwrapResultExtensions
{
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public static async ValueTask OrThrow(this ValueTask<IOResult> self)
    {
        if (self.IsCompletedSuccessfully)
        {
            IOResult completedResult = self.Result;
            if (completedResult != IOResult.Ok)
            {
                throw new ChannelClosedException(completedResult);
            }

            return;
        }

        IOResult result = await self;

        if (result != IOResult.Ok)
        {
            throw new ChannelClosedException(result);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<ReadResult> OrThrow(this ValueTask<ReadResult> self)
    {
        if (self.IsCompletedSuccessfully)
        {
            ReadResult completedResult = self.Result;
            if (completedResult.Result != IOResult.Ok)
            {
                throw new ChannelClosedException(completedResult.Result);
            }

            return completedResult;
        }

        ReadResult result = await self;

        if (result.Result != IOResult.Ok)
        {
            throw new ChannelClosedException(result.Result);
        }
        else
        {
            return result;
        }
    }
}
