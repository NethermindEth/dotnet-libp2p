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
    INewConnectionContext CreateConnection<TProtocol>() where TProtocol : IProtocol;
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
    Task<IChannel> OpenStreamAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionListenerProtocol;
    Task<IChannel> OpenStreamAsync(ISessionListenerProtocol protocol, CancellationToken token = default);
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
    IAsyncEnumerable<UpgradeOptions> DialRequests { get; }
}

public class State
{
    public Multiaddress? LocalAddress { get; set; }
    public Multiaddress? RemoteAddress { get; set; }
    public PublicKey? RemotePublicKey { get; set; }
    public PeerId? RemotePeerId => RemoteAddress?.GetPeerId();
}
