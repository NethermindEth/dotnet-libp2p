// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public class ChannelNegotiationRequest : IChannelNegotiationRequest
{
    public ChannelNegotiationRequest(IEnumerable<IProtocol> protocols, TaskCompletionSource? completionSource = default)
    {
        Protocols = protocols;
        CompletionSource = completionSource;
    }

    public ChannelNegotiationRequest(IProtocol protocol, TaskCompletionSource? completionSource = default)
    {
        Protocols = new IProtocol[] { protocol };
        CompletionSource = completionSource;
    }

    public IEnumerable<IProtocol> Protocols { get; init; }
    public TaskCompletionSource? CompletionSource { get; init; }

    public override string ToString()
    {
        return $"Request for {string.Join(",", Protocols.Select(p => p.Id))}";
    }
}
