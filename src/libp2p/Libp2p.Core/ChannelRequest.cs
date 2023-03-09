// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public class ChannelRequest : IChannelRequest
{
    public IProtocol? SubProtocol { get; init; }
    public TaskCompletionSource? CompletionSource { get; init; }

    public override string ToString()
    {
        return $"Requesst for {SubProtocol?.Id ?? "unknown protocol"}";
    }
}
