// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public class ChannelRequest : IChannelRequest
{
    public IProtocol? SubProtocol { get; init; }
    public TaskCompletionSource<bool>? CompletionSource { get; init; }

    /// <summary>
    /// Can read multiple writes at a time
    /// </summary>
    public bool IsStream { get; init; } = true;

    public override string ToString()
    {
        return $"Request for {SubProtocol?.Id ?? "unknown protocol"}";
    }
}
