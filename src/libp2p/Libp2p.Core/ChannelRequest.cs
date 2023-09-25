// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public class ChannelRequest : IChannelRequest
{
    public ChannelRequest(IProtocol protocol, TaskCompletionSource? completionSource = default)
    {
        Protocol = protocol;
        CompletionSource = completionSource;
    }

    public IProtocol Protocol { get; }

    public TaskCompletionSource? CompletionSource { get; init; }

    public override string ToString()
    {
        return $"Request for {Protocol.Id}";
    }
}
