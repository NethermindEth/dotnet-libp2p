// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core;

public interface ILocalPeer : IAsyncDisposable
{
    Identity Identity { get; }

    /// <summary>Gets a read-only snapshot of registered inbound and outbound sessions.</summary>
    /// <remarks>
    /// Each read captures membership independently of subsequent connects and disconnects.
    /// Registered sessions have a remote peer identity and address. This includes sessions that are
    /// still initializing. Use <see cref="ISession.IsInitialized"/> to check readiness, <see cref="OnConnected"/>
    /// to observe successful initialization, and <see cref="ISession.Closed"/> to observe the start of closure.
    /// A closing session can still appear until removal completes.
    /// </remarks>
    IReadOnlyList<ISession> Sessions { get; }

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

    /// <summary>Gets an installed protocol matching the type and wire identifier.</summary>
    /// <remarks>The default implementation checks the result of <see cref="GetProtocol{T}()"/>.</remarks>
    T? GetProtocol<T>(string protocolId) where T : class, IProtocol
        => GetProtocol<T>() is { } protocol && protocol.Id == protocolId ? protocol : null;

    ObservableCollection<Multiaddress> ListenAddresses { get; }

    event Connected? OnConnected;
}

public delegate void Connected(ISession newSession);
