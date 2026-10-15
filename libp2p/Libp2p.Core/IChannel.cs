// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public interface IChannel : IReader, IWriter
{
    ValueTask CloseAsync();

    /// <summary>Aborts both directions, including any data pending on the channel.</summary>
    ValueTask AbortAsync();

    /// <summary>
    /// Stops reading: pending and future writes from the other side fail, while data already
    /// written to it stays readable. Implementations without half-close close both directions.
    /// </summary>
    ValueTask CloseReadAsync() => CloseAsync();
    TaskAwaiter GetAwaiter();

    /// <summary>Gets a token canceled when this channel completes.</summary>
    /// <remarks>
    /// The default implementation allocates a cancellation source and completion registration on each access.
    /// Both the source and the captured execution context are retained until the channel completes.
    /// Acquire the token once and reuse it for the channel lifetime.
    /// </remarks>
    CancellationToken CancellationToken
    {
        get
        {
            CancellationTokenSource cts = new();
            GetAwaiter().OnCompleted(cts.Cancel);
            return cts.Token;
        }
    }
}
