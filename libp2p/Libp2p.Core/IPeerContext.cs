// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Dto;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public interface ITransportContext
{
    ILocalPeer Peer { get; }
    void ListenerReady(Multiaddress addr);
    INewConnectionContext CreateConnection();
    Activity? Activity { get; }
}

public interface IContextState
{
    string Id { get; }
    State State { get; }
}

public interface IConnectionContext : ITransportContext, IChannelFactory, IContextState
{
    UpgradeOptions? UpgradeOptions { get; }
    Task DisconnectAsync();
    INewSessionContext UpgradeToSession();
}

public interface ISessionContext : IConnectionContext
{
    Task DialAsync<TProtocol>() where TProtocol : ISessionProtocol;
    Task DialAsync(ISessionProtocol protocol);
    Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>;
}


public interface INewConnectionContext : IDisposable, IChannelFactory, IContextState
{
    ILocalPeer Peer { get; }
    CancellationToken Token { get; }
    INewSessionContext UpgradeToSession();
    Activity? Activity { get; }
}

public interface INewSessionContext : IDisposable, INewConnectionContext
{
    IEnumerable<UpgradeOptions> DialRequests { get; }
}

public class State
{
    public Multiaddress? LocalAddress { get; set; }
    public Multiaddress? RemoteAddress { get; set; }
    public PublicKey? RemotePublicKey { get; set; }
    public PeerId? RemotePeerId => RemoteAddress?.GetPeerId();

    /// <summary>
    /// Largest write the layer below carries as a single unit, when it has such a size. A secure
    /// channel that packs each write into one record sets this so the layer above can size its
    /// frames to fit, instead of every frame spilling into a second record. Larger writes are
    /// still accepted; they are just split further down.
    /// </summary>
    public int? PreferredWriteSize { get; set; }
}
