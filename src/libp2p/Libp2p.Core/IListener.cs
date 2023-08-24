// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public interface IListener
{
    Multiaddr Address { get; }

    event OnConnection OnConnection;
    Task DisconnectAsync();
    TaskAwaiter GetAwaiter();
}

public delegate Task OnConnection(IRemotePeer peer);
