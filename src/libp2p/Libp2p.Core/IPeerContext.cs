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
}
