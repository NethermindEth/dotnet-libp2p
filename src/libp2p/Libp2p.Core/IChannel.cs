// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public interface IChannel : IReader, IWriter
{
    ValueTask CloseAsync();
    TaskAwaiter GetAwaiter();

    CancellationToken CancellationToken
    {
        get
        {
            CancellationTokenSource token = new();
            GetAwaiter().OnCompleted(token.Cancel);
            return token.Token;
        }
    }
}
