// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public interface ILocalPeer : IPeer
{
    Task<IRemotePeer> DialAsync(Multiaddress addr, CancellationToken token = default);
    Task<IListener> ListenAsync(Multiaddress addr, CancellationToken token = default);
}
