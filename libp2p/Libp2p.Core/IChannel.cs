// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public interface IChannel : IReader, IWriter
{
    ValueTask CloseAsync();

    /// <summary>Aborts both directions, including any data pending on the channel.</summary>
    ValueTask AbortAsync();
    TaskAwaiter GetAwaiter();

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
