// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Libp2p.Core.TestsBase;

public class IncrementNumberTestProtocol(int? delay = null, bool cancelOnToken = true) : ISessionProtocol<int, int>
{
    public string Id => "/number/";

    public async Task<int> DialAsync(IChannel downChannel, ISessionContext context, int request)
    {
        await downChannel.WriteVarintAsync(request);
        if (delay is not null) await Task.Delay(delay.Value);
        return await downChannel.ReadVarintAsync();
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        int request = await downChannel.ReadVarintAsync();
        await downChannel.WriteVarintAsync(request + 1);
    }
}
