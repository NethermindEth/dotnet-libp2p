// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core.Context;

public class ContextBase(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions) : IChannelFactory
{
    protected bool isListener = isListener;
    public ILocalPeer Peer => localPeer;
    public State State => session.State;

    public IEnumerable<IProtocol> SubProtocols => localPeer.GetProtocolsFor(protocol);

    public string Id { get; } = session.Id;

    protected LocalPeer localPeer = localPeer;
    protected LocalPeer.Session session = session;
    protected ProtocolRef protocol = protocol;
    protected UpgradeOptions? upgradeOptions = upgradeOptions;

    public IChannel Upgrade(UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(session, protocol, null, upgradeOptions ?? this.upgradeOptions, isListener);
    }

    public IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(session, protocol, specificProtocol, upgradeOptions ?? this.upgradeOptions, isListener);
    }

    public Task Upgrade(IChannel parentChannel, UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(session, parentChannel, protocol, null, upgradeOptions ?? this.upgradeOptions, isListener);
    }

    public Task Upgrade(IChannel parentChannel, IProtocol specificProtocol, UpgradeOptions? upgradeOptions = null)
    {
        return localPeer.Upgrade(session, parentChannel, protocol, specificProtocol, upgradeOptions ?? this.upgradeOptions, isListener);
    }

    public INewConnectionContext CreateConnection()
    {
        return localPeer.CreateConnection(protocol, null, isListener);
    }

    public INewSessionContext UpgradeToSession()
    {
        return localPeer.UpgradeToSession(session, protocol, isListener);
    }

    public void ListenerReady(Multiaddress addr)
    {
        localPeer.ListenerReady(this, addr);
    }
}
