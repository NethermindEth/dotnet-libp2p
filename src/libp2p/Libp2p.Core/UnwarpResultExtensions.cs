// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Exceptions;
using System.Buffers;

namespace Nethermind.Libp2p.Core;
public static class UnwarpResultExtensions
{
    public static async ValueTask OrThrow(this ValueTask<IOResult> self)
    {
        if (self.IsCompleted && self.Result != IOResult.Ok)
        {
            throw new ChannelClosedException();
        }

        IOResult result = await self.AsTask();

        if (result != IOResult.Ok)
        {
            throw new ChannelClosedException();
        }
    }
    public static async ValueTask<ReadOnlySequence<byte>> OrThrow(this ValueTask<ReadResult> self)
    {
        if (self.IsCompleted && self.Result.Result != IOResult.Ok)
        {
            throw new ChannelClosedException();
        }

        ReadResult result = await self.AsTask();

        if (result.Result != IOResult.Ok)
        {
            throw new ChannelClosedException();
        }
        else
        {
            return result.Data;
        }
    }
}
