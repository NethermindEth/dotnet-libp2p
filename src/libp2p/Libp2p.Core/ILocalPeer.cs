// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core;

public interface ILocalPeer : IAsyncDisposable
{
    Identity Identity { get; }

    Task<ISession> DialAsync(Multiaddress addr, CancellationToken token = default);
    Task<ISession> DialAsync(Multiaddress[] samePeerAddrs, CancellationToken token = default);

    /// <summary>
    /// Find existing session or dial a peer if found in peer store
    /// </summary>
    Task<ISession> DialAsync(PeerId peerId, CancellationToken token = default);

    Task StartListenAsync(Multiaddress[]? addrs = default, CancellationToken token = default);


    ObservableCollection<Multiaddress> ListenAddresses { get; }

    event Connected? OnConnected;
}

public delegate void Connected(ISession newSession);
