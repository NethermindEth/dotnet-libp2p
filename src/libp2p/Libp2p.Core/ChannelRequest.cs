// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT


namespace Nethermind.Libp2p.Core;

internal class ChannelRequest : IChannelRequest
{
    public Func<IId, IChannel, IChannelFactory?, IPeerContext, Task>? Call { get; init; }
    public Action<Task>? SetResult { get; init; }

    public IId? SubProtocol { get; init; }
    //public TaskCompletionSource<object>? CompletionSource { get; init; }

    public override string ToString()
    {
        return $"Request for {SubProtocol?.Id ?? "unknown protocol"}";
    }
}
