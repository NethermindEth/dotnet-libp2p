// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Hash;
using Nethermind.Libp2p.Protocols.WebRtc;
using SIPSorcery.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class DtlsFingerprintTests
{
    [Test]
    public void SdpRoundtrip_Works()
    {
        DtlsFingerprint input = new("sha-256", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        string sdp = input.ToSdpString();

        DtlsFingerprint parsed = DtlsFingerprint.ParseFromSdp(sdp);

        Assert.That(parsed.Algorithm, Is.EqualTo("sha-256"));
        Assert.That(parsed.Value, Is.EqualTo(input.Value));
    }

    [Test]
    public void MultihashRoundtrip_Works()
    {
        DtlsFingerprint input = new("sha-256", Enumerable.Range(10, 32).Select(i => (byte)i).ToArray());
        byte[] multihash = input.ToMultihashBytes();

        DtlsFingerprint parsed = DtlsFingerprint.ParseFromMultihash(multihash);

        Assert.That(parsed.Algorithm, Is.EqualTo("sha-256"));
        Assert.That(parsed.Value, Is.EqualTo(input.Value));
    }

    [Test]
    public void Multihash_IsSha256Code()
    {
        DtlsFingerprint input = new("sha-256", Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        Multihash decoded = Multihash.Decode(input.ToMultihashBytes());

        Assert.That(decoded.Code, Is.EqualTo(HashType.SHA2_256));
    }

    [Test]
    public void FromRtcFingerprint_ParsesAlgorithmAndValue()
    {
        RTCDtlsFingerprint rtc = new()
        {
            algorithm = "sha-256",
            value = "01:02:03:04:05:06:07:08:09:0A:0B:0C:0D:0E:0F:10:11:12:13:14:15:16:17:18:19:1A:1B:1C:1D:1E:1F:20"
        };

        DtlsFingerprint parsed = DtlsFingerprint.FromRtcFingerprint(rtc);

        Assert.That(parsed.Algorithm, Is.EqualTo("sha-256"));
        Assert.That(parsed.Value, Is.EqualTo(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
    }
}