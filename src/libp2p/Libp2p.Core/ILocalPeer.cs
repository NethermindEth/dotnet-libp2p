// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core;

public interface ILocalPeer : IAsyncDisposable
{
    Identity Identity { get; }

    /// <summary>
    /// Gets the peer-owned live view of current sessions.
    /// Sessions are registered during session upgrade and removed when disconnected.
    /// </summary>
    IReadOnlyCollection<ISession> Sessions { get; }

    Task<ISession> DialAsync(Multiaddress addr, CancellationToken token = default);
    Task<ISession> DialAsync(Multiaddress[] samePeerAddrs, CancellationToken token = default);

    /// <summary>
    /// Find existing session or dial a peer if found in peer store
    /// </summary>
    Task<ISession> DialAsync(PeerId peerId, CancellationToken token = default);

    Task StartListenAsync(Multiaddress[]? addrs = default, CancellationToken token = default);

    /// <summary>
    /// Get a protocol instance by type.
    /// </summary>
    /// <typeparam name="T">The protocol type to retrieve.</typeparam>
    /// <returns>The protocol instance, or null if not found.</returns>
    T? GetProtocol<T>() where T : class, IProtocol;

    ObservableCollection<Multiaddress> ListenAddresses { get; }

    event Connected? OnConnected;
}

public delegate void Connected(ISession newSession);
