// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IChannelFactory
{
    IEnumerable<IProtocol> SubProtocols { get; }

    IChannel Upgrade(UpgradeOptions? options = null);
    IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? options = null);

    Task Upgrade(IChannel parentChannel, UpgradeOptions? options = null);
    Task Upgrade(IChannel parentChannel, IProtocol specificProtocol, UpgradeOptions? options = null);
}

public record UpgradeOptions
{
    public IProtocol? SelectedProtocol { get; init; }
    public UpgradeModeOverride ModeOverride { get; init; }
    public TaskCompletionSource<object?>? CompletionSource { get; init; }

    /// <summary>Cancellation of this stream invocation.</summary>
    /// <remarks>
    /// In a session protocol context this links the caller's token (for outbound calls) with
    /// the session lifetime. Inbound invocations observe session closure. The token is valid
    /// until the invocation returns; use <see cref="IConnectionContext.Token"/> for session-scoped work.
    /// </remarks>
    public CancellationToken CancellationToken { get; init; }
    public object? Argument { get; set; }
}

public enum UpgradeModeOverride
{
    None,
    Dial,
    Listen,
}
