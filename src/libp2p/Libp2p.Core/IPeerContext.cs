// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Dto;

namespace Nethermind.Libp2p.Core;

public interface ITransportContext
{
    IPeer Peer { get; }
    void ListenerReady(Multiaddress addr);
    INewConnectionContext CreateConnection();
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
    IPeer Peer { get; }
    CancellationToken Token { get; }
    INewSessionContext UpgradeToSession();
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
}
