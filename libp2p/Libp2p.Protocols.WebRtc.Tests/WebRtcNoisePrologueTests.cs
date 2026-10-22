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

    /// <summary>
    /// The test vector published in the spec itself (webrtc-direct.md, "Both client and server use
    /// SHA-256"). The test above derives its expectation from the same ToMultihashBytes() the
    /// implementation calls, so it cannot catch a wrong multihash prefix or length byte; this one
    /// is an external oracle and can. Here the client is _A_ (the dialer, Noise responder) and the
    /// server is _B_ (the listener, Noise initiator), which fixes the argument order.
    /// </summary>
    [Test]
    public void Build_MatchesTheSpecTestVector()
    {
        DtlsFingerprint client = new("sha-256", Convert.FromHexString("3e79af40d6059617a0d83b83a52ce73b0c1f37a72c6043ad2969e2351bdca870"));
        DtlsFingerprint server = new("sha-256", Convert.FromHexString("30fc9f469c207419dfdd0aab5f27a86c973c94e40548db9375cca2e915973b99"));

        byte[] prologue = WebRtcNoisePrologue.Build(client, server);

        Assert.That(
            Convert.ToHexString(prologue).ToLowerInvariant(),
            Is.EqualTo(
                "6c69627032702d7765627274632d6e6f6973653a"
                + "12203e79af40d6059617a0d83b83a52ce73b0c1f37a72c6043ad2969e2351bdca870"
                + "122030fc9f469c207419dfdd0aab5f27a86c973c94e40548db9375cca2e915973b99"));
    }
}
