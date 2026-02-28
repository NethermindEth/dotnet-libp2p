// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;

namespace Nethermind.Libp2p.Protocols.Quic.Tests;

public class ProtocolTests
{
    [Test]
    public async Task Test_CreateProtocol()
    {
        CancellationTokenSource cts = new();
        QuicProtocol proto = new();

        LocalPeer peer = new(new Identity(), new PeerStore(), new ProtocolStackSettings(), null);
        ITransportContext context = new TransportContext(peer, new ProtocolRef(proto), true, null);
        _ = new QuicProtocol().ListenAsync(context, "/ip4/127.0.0.1/udp/0", cts.Token);
        await Task.Delay(1000);
        cts.Cancel();
    }
}
