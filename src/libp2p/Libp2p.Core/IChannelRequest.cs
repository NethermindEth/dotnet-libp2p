// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IChannelRequest
{
    IProtocol Protocol { get; }
    public TaskCompletionSource? CompletionSource { get; }
}

