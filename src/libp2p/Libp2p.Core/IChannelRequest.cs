// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IChannelRequest
{
    IProtocol? SubProtocol { get; }
    public TaskCompletionSource<bool>? CompletionSource { get; }
    bool IsStream { get; }
}
