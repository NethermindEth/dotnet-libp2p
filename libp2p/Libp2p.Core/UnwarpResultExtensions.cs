// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Exceptions;
using System.Buffers;

namespace Nethermind.Libp2p.Core;

public static class UnwrapResultExtensions
{
    /// <summary>Checks a read result without releasing its ownership lease.</summary>
    public static ReadOnlySequence<byte> OrThrow(this ReadResult result)
    {
        if (result.Result != IOResult.Ok) throw new ChannelClosedException(result.Result);
        return result.Data;
    }

    public static async ValueTask OrThrow(this ValueTask<IOResult> self)
    {
        IOResult result = await self.ConfigureAwait(false);
        if (result != IOResult.Ok)
        {
            throw new ChannelClosedException(result);
        }
    }
    /// <summary>Returns a stable sequence, detaching pooled storage before releasing its lease.</summary>
    /// <remarks>To avoid a compatibility copy, use a using declaration for the awaited result and call its synchronous OrThrow overload.</remarks>
    public static async ValueTask<ReadOnlySequence<byte>> OrThrow(this ValueTask<ReadResult> self)
    {
        using ReadResult result = await self.ConfigureAwait(false);
        ReadOnlySequence<byte> data = result.OrThrow();
        return result.IsOwned ? new ReadOnlySequence<byte>(data.ToArray()) : data;
    }
}
