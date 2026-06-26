// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IPeerFactory : IAsyncDisposable
{
    ILocalPeer Create(Identity? identity = default);

    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
}
