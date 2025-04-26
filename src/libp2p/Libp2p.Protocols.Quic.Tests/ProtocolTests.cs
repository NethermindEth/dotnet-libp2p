// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Quic.Tests;

public class ProtocolTests
{
    [Test]
    public async Task Test_CreateProtocol()
    {
        CancellationTokenSource cts = new();
        QuicProtocol proto = new();
        _ = new QuicProtocol().ListenAsync(new TransportContext(new LocalPeer(new Identity(), new Core.Discovery.PeerStore(), new ProtocolStackSettings(), null), new ProtocolRef(proto), true, null), "/ip4/127.0.0.1/udp/0", cts.Token);
        await Task.Delay(1000);
        cts.Cancel();
    }
}
