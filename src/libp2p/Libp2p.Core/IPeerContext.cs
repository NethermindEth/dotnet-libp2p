// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public interface IContext
{
    string Id { get; }
    IPeer Peer { get; }
}

public class Remote
{
    public Multiaddress? Address { get; set; }
    public Identity? Identity { get; set; }
}

public interface ITransportContext : IContext
{
    void ListenerReady(Multiaddress addr);
    ITransportConnectionContext CreateConnection();
}

public interface ITransportConnectionContext : IDisposable, IChannelFactory, IContext
{
    Remote Remote { get; }
    CancellationToken Token { get; }
    IConnectionSessionContext CreateSession();
}

public interface IConnectionContext : IChannelFactory, IContext
{
    UpgradeOptions? UpgradeOptions { get; }
    Remote Remote { get; }
    Task DisconnectAsync();
    IConnectionSessionContext CreateSession();
}

public interface IConnectionSessionContext : IDisposable
{
    Remote Remote { get; }
    string Id { get; }
    IEnumerable<ChannelRequest> DialRequests { get; }
}

public interface ISessionContext : IChannelFactory, IContext
{
    UpgradeOptions? UpgradeOptions { get; }
    Remote Remote { get; }
    Task DialAsync<TProtocol>() where TProtocol : ISessionProtocol;
    Task DialAsync(ISessionProtocol[] protocols);
    Task DisconnectAsync();
}
