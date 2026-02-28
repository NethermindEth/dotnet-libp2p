// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols;

public class RelayStopProtocol : ISessionProtocol
{
    public string Id => "/libp2p/circuit/relay/0.2.0/stop";

    public Task DialAsync(IChannel downChannel, ISessionContext context)
    {
        throw new NotImplementedException();
    }

    public Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        throw new NotImplementedException();
    }
}
