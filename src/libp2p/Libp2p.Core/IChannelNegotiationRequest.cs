// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IChannelNegotiationRequest
{
    IEnumerable<IProtocol> Protocols { get; }
    public TaskCompletionSource? CompletionSource { get; }
}
