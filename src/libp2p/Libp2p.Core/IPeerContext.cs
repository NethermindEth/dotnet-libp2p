// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core;

public interface IContext
{
    string Id { get; }
    IPeer Peer { get; }
}

public interface IConnectionContext
{
    string Id { get; }
    IPeer Peer { get; }
    IRemotePeer RemotePeer { get; }
}

public interface IRemotePeer
{
    Identity Identity { get; set; }
    Multiaddress Address { get; set; }
}



public interface ITransportContext : IContext
{
    void ListenerReady(Multiaddress addr);
    ITransportConnectionContext CreateConnection();
}

public interface ITransportConnectionContext : IDisposable, IChannelFactory, IContext
{
    CancellationToken Token { get; }
    IConnectionSessionContext CreateSession(PeerId peerId);
}

public interface IConnectionContext : IChannelFactory, IContext
{
    Task DisconnectAsync();
    IConnectionSessionContext CreateSession(PeerId peerId);
}

public interface IConnectionSessionContext : IDisposable
{
    string Id { get; }
    IEnumerable<IChannelRequest> DialRequests { get; }
}

public interface ISessionContext : IChannelFactory, IContext
{
    Task DialAsync<TProtocol>() where TProtocol: ISessionProtocol;
    Task DialAsync(ISessionProtocol[] protocols);
    Task DisconnectAsync();
}
