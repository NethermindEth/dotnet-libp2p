// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface ILocalPeer : IPeer
{
    Task<IRemotePeer> DialAsync(Multiaddr addr, CancellationToken token = default);
    Task<IListener> ListenAsync(Multiaddr addr, CancellationToken token = default);
}
