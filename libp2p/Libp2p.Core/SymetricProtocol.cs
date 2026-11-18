// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public abstract class SymmetricProtocol
{
    public Task DialAsync(IChannel channel, IConnectionContext context)
    {
        return ConnectAsync(channel, context, false);
    }

    public Task ListenAsync(IChannel channel, IConnectionContext context)
    {
        return ConnectAsync(channel, context, true);
    }

    protected abstract Task ConnectAsync(IChannel channel, IConnectionContext context, bool isListener);
}
public abstract class SymmetricSessionProtocol
{
    public Task DialAsync(IChannel channel, ISessionContext context)
    {
        return ConnectAsync(channel, context, false);
    }

    public Task ListenAsync(IChannel channel, ISessionContext context)
    {
        return ConnectAsync(channel, context, true);
    }

    /// <summary>Runs one stream invocation in either direction.</summary>
    /// <remarks>
    /// Observe <see cref="UpgradeOptions.CancellationToken"/> on <see cref="IConnectionContext.UpgradeOptions"/>
    /// and pass it explicitly to nested dials and asynchronous work. Protocol instances may serve concurrent streams.
    /// </remarks>
    protected abstract Task ConnectAsync(IChannel channel, ISessionContext context, bool isListener);
}
