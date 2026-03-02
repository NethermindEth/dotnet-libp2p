// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core.Context;

public class ContextBase(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions, ActivitySource? activitySource, Activity? activity) : IChannelFactory
{
    protected bool isListener = isListener;
    public ILocalPeer Peer => localPeer;
    public State State => session.State;

    public ActivitySource? ActivitySource { get; } = activitySource;

    public Activity? Activity { get; } = activity;


    public IEnumerable<IProtocol> SubProtocols => localPeer.GetProtocolsFor(protocol);

    public string Id { get; } = session.Id;

    protected LocalPeer localPeer = localPeer;
    protected LocalPeer.Session session = session;
    protected ProtocolRef protocol = protocol;
    protected UpgradeOptions? upgradeOptions = upgradeOptions;

    public IChannel Upgrade(UpgradeOptions? upgradeOptions = null)
        => localPeer.Upgrade(session, protocol, null, upgradeOptions ?? this.upgradeOptions, isListener, Activity);

    public IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? upgradeOptions = null)
        => localPeer.Upgrade(session, protocol, specificProtocol, upgradeOptions ?? this.upgradeOptions, isListener, Activity);

    public Task Upgrade(IChannel parentChannel, UpgradeOptions? upgradeOptions = null)
        => localPeer.Upgrade(session, parentChannel, protocol, null, upgradeOptions ?? this.upgradeOptions, isListener, Activity);

    public Task Upgrade(IChannel parentChannel, IProtocol specificProtocol, UpgradeOptions? upgradeOptions = null)
        => localPeer.Upgrade(session, parentChannel, protocol, specificProtocol, upgradeOptions ?? this.upgradeOptions, isListener, Activity);

    public INewConnectionContext CreateConnection()
        => localPeer.CreateConnection(protocol, null, isListener, Activity);

    public INewSessionContext UpgradeToSession()
        => localPeer.UpgradeToSession(session, protocol, isListener, Activity);

    public void ListenerReady(Multiaddress addr)
        => localPeer.ListenerReady(this, addr);
}
