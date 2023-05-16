// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IRemotePeer : IPeer
{
    Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol;
    Task DisconnectAsync();
}
