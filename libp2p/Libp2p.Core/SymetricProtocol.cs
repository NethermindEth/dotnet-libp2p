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

    protected abstract Task ConnectAsync(IChannel channel, ISessionContext context, bool isListener);
}
