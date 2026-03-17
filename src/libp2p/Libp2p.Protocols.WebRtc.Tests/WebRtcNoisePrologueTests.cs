// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using System.Text;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcNoisePrologueTests
{
    [Test]
    public void Build_UsesExpectedPrefixAndOrder()
    {
        DtlsFingerprint dialer = new("sha-256", Enumerable.Repeat((byte)0xAA, 32).ToArray());
        DtlsFingerprint listener = new("sha-256", Enumerable.Repeat((byte)0xBB, 32).ToArray());

        byte[] prologue = WebRtcNoisePrologue.Build(dialer, listener);
        byte[] prefix = Encoding.ASCII.GetBytes("libp2p-webrtc-noise:");
        byte[] expected = [.. prefix, .. dialer.ToMultihashBytes(), .. listener.ToMultihashBytes()];

        Assert.That(prologue, Is.EqualTo(expected));
    }
}