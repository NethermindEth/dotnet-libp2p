// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public class TransportContext : ITransportContext
{
    private readonly LocalPeer _peer;
    private readonly ProtocolRef _proto;

    public TransportContext(LocalPeer peer, ProtocolRef proto, bool isListener, Activity? activity)
    {
        _peer = peer;
        _proto = proto;
        IsListener = isListener;
        Activity = activity;
    }

    public Identity Identity => _peer.Identity;
    public ILocalPeer Peer => _peer;
    public bool IsListener { get; }
    public Activity? Activity { get; }

    public void ListenerReady(Multiaddress addr)
    {
        _peer.ListenerReady(this, addr);
    }

    public virtual INewConnectionContext CreateConnection()
    {
        return _peer.CreateConnection(_proto, null, IsListener, Activity);
    }
}

public class DialerTransportContext : TransportContext
{
    private readonly LocalPeer _peer;
    private readonly LocalPeer.Session _session;
    private readonly ProtocolRef _proto;
    private readonly Activity? _activity;

    public DialerTransportContext(LocalPeer peer, LocalPeer.Session session, ProtocolRef proto, Activity? activity)
        : base(peer, proto, false, activity)
    {
        _peer = peer;
        _session = session;
        _proto = proto;
        _activity = activity;
    }

    public override INewConnectionContext CreateConnection()
    {
        return _peer.CreateConnection(_proto, _session, false, _activity);
    }
}
