// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Dto;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public interface ITransportContext
{
    ILocalPeer Peer { get; }
    /// <summary>Scope shared by listeners whose inbound TCP connections share an allowance.</summary>
    object ConnectionAdmissionScope => this;
    PeerResourceLimits ResourceLimits => PeerResourceLimits.Default;
    TimeProvider Clock => TimeProvider.System;
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
    /// <summary>Lifetime of the connection. Legacy contexts may omit lifetime notifications.</summary>
    CancellationToken Token => CancellationToken.None;
    UpgradeOptions? UpgradeOptions { get; }
    Task DisconnectAsync();
    INewSessionContext UpgradeToSession();
}

public interface ISessionContext : IConnectionContext
{
    Task DialAsync<TProtocol>() where TProtocol : ISessionProtocol;
    Task DialAsync(ISessionProtocol protocol);

    /// <summary>Dials a session protocol without a request payload and returns its response.</summary>
    /// <remarks>The default implementation does not support typed responses. Implementations must override this method to return protocol results.</remarks>
    /// <exception cref="NotSupportedException">The session implementation does not support typed responses.</exception>
    Task<TResponse> DialAsync<TProtocol, TResponse>(CancellationToken token = default) where TProtocol : ISessionProtocol<TResponse>
        => Task.FromException<TResponse>(new NotSupportedException("This session does not support typed protocol responses."));

    Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>;
}


public interface INewConnectionContext : IDisposable, IChannelFactory, IContextState
{
    ILocalPeer Peer { get; }
    CancellationToken Token { get; }
    /// <summary>Completes after owned protocol upgrades stop, following disposal.</summary>
    Task Completion => Task.CompletedTask;
    /// <summary>Disconnects an established connection only if it has had no application operations for the given interval.</summary>
    /// <remarks>Legacy contexts without idle tracking remain ineligible for eviction.</remarks>
    bool TryDisconnectIfIdle(TimeSpan idleTimeout) => false;
    INewSessionContext UpgradeToSession();
    Activity? Activity { get; }
}

public interface INewSessionContext : IDisposable, INewConnectionContext
{
    IEnumerable<UpgradeOptions> DialRequests { get; }
}

public class State
{
    internal Lock PeerRecordLock { get; } = new();
    private ulong? _verifiedPeerRecordSequence;
    public Multiaddress? LocalAddress { get; set; }
    public Multiaddress? RemoteAddress { get; set; }
    public PublicKey? RemotePublicKey { get; set; }
    public PeerId? RemotePeerId => RemoteAddress?.GetPeerId();

    /// <summary>Highest signed peer record sequence verified for this session's remote peer.</summary>
    public ulong? VerifiedPeerRecordSequence
    {
        get
        {
            lock (PeerRecordLock) return _verifiedPeerRecordSequence;
        }
    }

    public void RecordVerifiedPeerRecordSequence(ulong sequence)
    {
        lock (PeerRecordLock)
        {
            if (_verifiedPeerRecordSequence is null || sequence > _verifiedPeerRecordSequence)
            {
                _verifiedPeerRecordSequence = sequence;
            }
        }
    }

    /// <summary>
    /// Largest write the layer below carries as a single unit, when it has such a size. A secure
    /// channel that packs each write into one record sets this so the layer above can size its
    /// frames to fit, instead of every frame spilling into a second record. Larger writes are
    /// still accepted; they are just split further down.
    /// </summary>
    public int? PreferredWriteSize { get; set; }
}
