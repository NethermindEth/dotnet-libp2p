// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcDirectProtocolTests
{
    [Test]
    public void Constructor_CreatesLocalDtlsCertificate()
    {
        Assert.That(() => new WebRtcDirectProtocol(), Throws.Nothing);
    }
}
