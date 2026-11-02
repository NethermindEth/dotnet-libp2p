// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Nethermind.Libp2p.Core.Context;

public class SessionContext(LocalPeer localPeer, LocalPeer.Session session, ProtocolRef protocol, bool isListener, UpgradeOptions? upgradeOptions,
    ActivitySource? activitySource, Activity? parentActivity)
    : ContextBase(localPeer, session, protocol, isListener, upgradeOptions, activitySource, parentActivity), ISessionContext
{
    public CancellationToken Token => session.ConnectionToken;

    public UpgradeOptions? UpgradeOptions => upgradeOptions;

    public async Task DialAsync<TProtocol>() where TProtocol : ISessionProtocol
    {
        await session.DialAsync<TProtocol>();
    }

    public async Task DialAsync(ISessionProtocol protocol)
    {
        await session.DialAsync(protocol);
    }

    public Task<TResponse> DialAsync<TProtocol, TResponse>(CancellationToken token = default) where TProtocol : ISessionProtocol<TResponse>
    {
        return session.DialAsync<TProtocol, TResponse>(token);
    }

    public Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>
    {
        return session.DialAsync<TProtocol, TRequest, TResponse>(request, token);
    }

    public Task DisconnectAsync()
    {
        return session.DisconnectAsync();
    }
}
