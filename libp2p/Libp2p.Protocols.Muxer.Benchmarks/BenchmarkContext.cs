// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Muxer.Benchmarks;

/// <summary>Connection and session context that hands every stream upgrade to a callback.</summary>
internal sealed class BenchmarkContext(
    string id,
    CancellationToken token,
    IEnumerable<UpgradeOptions>? dialRequests = null,
    Func<UpgradeOptions?, IChannel>? upgrade = null,
    IEnumerable<IProtocol>? subProtocols = null,
    UpgradeOptions? upgradeOptions = null) : IConnectionContext, INewSessionContext
{
    public string Id => id;
    public State State { get; } = new();
    public CancellationToken Token => token;
    public UpgradeOptions? UpgradeOptions => upgradeOptions;
    public IEnumerable<UpgradeOptions> DialRequests => dialRequests ?? [];
    public IEnumerable<IProtocol> SubProtocols => subProtocols ?? [];
    public ILocalPeer Peer => throw new NotSupportedException();
    public Activity? Activity => null;

    public INewSessionContext UpgradeToSession() => this;

    public IChannel Upgrade(UpgradeOptions? options = null) =>
        (upgrade ?? throw new NotSupportedException())(options);

    public IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? options = null) => Upgrade(options);

    public Task Upgrade(IChannel parentChannel, UpgradeOptions? options = null) => Task.CompletedTask;

    public Task Upgrade(IChannel parentChannel, IProtocol specificProtocol, UpgradeOptions? options = null) => Task.CompletedTask;

    public void ListenerReady(Multiaddress addr) => throw new NotSupportedException();

    public INewConnectionContext CreateConnection() => throw new NotSupportedException();

    public Task DisconnectAsync() => Task.CompletedTask;

    public void Dispose()
    {
    }
}
