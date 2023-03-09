// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public interface IChannel
{
    IReader Reader { get; }
    IWriter Writer { get; }
    bool IsClosed { get; }
    CancellationToken Token { get; }
    Task CloseAsync(bool graceful = true);
    void OnClose(Func<Task> action);
    TaskAwaiter GetAwaiter();
}
