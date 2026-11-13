// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using SIPSorcery.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcDirectStunTests
{
    /// <summary>
    /// Captured off the wire from SIPSorcery's own ICE agent, so it is an oracle independent of the code
    /// under test: a binding request whose USERNAME is
    /// <c>libp2p+webrtc+v2/UASIXOHKNGWOMLETGBKWCLJX:PSAY</c>, carrying PRIORITY, ICE-CONTROLLING,
    /// MESSAGE-INTEGRITY and FINGERPRINT.
    /// </summary>
    private const string CapturedRequestHex =
        "000100682112A442435A534547574556415842450006002E6C69627032702B7765627274" +
        "632B76322F55415349584F484B4E47574F4D4C455447424B57434C4A583A50534159000000" +
        "240004FF2A007E802A0008B109AA030F4DFAED00080014D9F8D6AC10F4B0B4C6F26A3E1A95" +
        "DCFBD055DA148028000449DA0EFD";

    /// <summary>
    /// The success response that same agent accepted for the request above. Its XOR-MAPPED-ADDRESS decodes
    /// to 127.0.0.1:52382 — the port is <c>0xED8C ^ 0x2112</c>, per RFC 5389 section 15.2.
    /// </summary>
    private const string CapturedResponseHex =
        "0101002C2112A442435A53454757455641584245002000080001ED8C5E12A44300080014" +
        "99B2B31D8EAA9823A797A4E24608984559D9E29E8028000416062BEB";

    private const string ServerUfrag = "libp2p+webrtc+v2/UASIXOHKNGWOMLETGBKWCLJX";
    private const string ClientUfrag = "PSAY";
    private const string ClientPassword = "UASIXOHKNGWOMLETGBKWCLJX";

    private static readonly IPEndPoint CapturedSource = new(IPAddress.Loopback, 52382);

    private static byte[] CapturedRequest => Convert.FromHexString(CapturedRequestHex);

    [Test]
    public void IsStunDatagram_RejectsEveryBufferTooShortToHoldAHeader()
    {
        // Truncations of the real request, not zero fill: from eight bytes up they carry the genuine magic
        // cookie, so the length bound is the only thing left to reject them. Zero-filled buffers would be
        // turned away by the cookie check instead and the bound would go untested — and the bound is the
        // one that matters, because SIPSorcery leaves the header null and dereferences it below 20 bytes.
        byte[] full = CapturedRequest;

        for (int length = 0; length < 20; length++)
        {
            byte[] datagram = full[..length];

            Assert.That(WebRtcDirectStun.IsStunDatagram(datagram), Is.False, $"length {length}");
            Assert.That(() => WebRtcDirectStun.TryParseBindingRequest(datagram, out _, out _),
                Throws.Nothing, $"length {length}");
            Assert.That(WebRtcDirectStun.TryParseBindingRequest(datagram, out _, out _), Is.False,
                $"length {length}");
        }
    }

    [Test]
    public void IsStunDatagram_RejectsAHeaderSizedBufferWithoutTheMagicCookie()
    {
        byte[] zeros = new byte[20];

        Assert.That(WebRtcDirectStun.IsStunDatagram(zeros), Is.False);
        Assert.That(() => WebRtcDirectStun.TryParseBindingRequest(zeros, out _, out _), Throws.Nothing);
        Assert.That(WebRtcDirectStun.TryParseBindingRequest(zeros, out _, out _), Is.False);
    }

    [Test]
    public void IsStunDatagram_RejectsADatagramWhoseTopTwoBitsAreNotZero()
    {
        // Both bits, not just the high one: 0x40 belongs to no RFC 7983 range and a check that only looked
        // at 0x80 would wave it through to the parser.
        foreach (byte first in new byte[] { 0x40, 0x80, 0xC0 })
        {
            byte[] datagram = CapturedRequest;
            datagram[0] = first;

            Assert.That(WebRtcDirectStun.IsStunDatagram(datagram), Is.False, $"first byte 0x{first:X2}");
            Assert.That(WebRtcDirectStun.TryParseBindingRequest(datagram, out _, out _), Is.False,
                $"first byte 0x{first:X2}");
        }
    }

    [Test]
    public void IsStunDatagram_AcceptsTheCapturedRequest()
    {
        Assert.That(WebRtcDirectStun.IsStunDatagram(CapturedRequest), Is.True);
    }

    [Test]
    public void TryParseBindingRequest_NeverThrowsForAnyTruncationOfARealRequest()
    {
        byte[] full = CapturedRequest;

        for (int length = 0; length <= full.Length; length++)
        {
            byte[] truncated = full[..length];
            Assert.That(() => WebRtcDirectStun.TryParseBindingRequest(truncated, out _, out _),
                Throws.Nothing, $"length {length}");
        }
    }

    [Test]
    public void TryParseBindingRequest_ReadsTheV2CredentialsOutOfTheCapturedRequest()
    {
        bool parsed = WebRtcDirectStun.TryParseBindingRequest(
            CapturedRequest, out STUNMessage? request, out WebRtcIceCredentials credentials);

        Assert.That(parsed, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(request, Is.Not.Null);
            Assert.That(credentials.Flow, Is.EqualTo(WebRtcDirectIceFlow.V2));
            Assert.That(credentials.ServerUfrag, Is.EqualTo(ServerUfrag));
            Assert.That(credentials.ClientUfrag, Is.EqualTo(ClientUfrag));
        });

        Assert.That(credentials.TryGetClientPassword(out string? clientPassword), Is.True);
        Assert.That(clientPassword, Is.EqualTo(ClientPassword));
    }

    [Test]
    public void TryParseBindingRequest_RejectsASuccessResponse()
    {
        byte[] response = Convert.FromHexString(CapturedResponseHex);

        Assert.That(WebRtcDirectStun.IsStunDatagram(response), Is.True);
        Assert.That(WebRtcDirectStun.TryParseBindingRequest(response, out _, out _), Is.False);
    }

    [Test]
    public void TryParseBindingRequest_RejectsAnIndicationCarryingTheSameUsername()
    {
        // The captured request with nothing changed but its message type. The USERNAME still parses and the
        // credentials still recover, so the header check is the only thing standing between an indication
        // and a signed success response — and an indication is never answered. Answering one would turn the
        // listener into a reflector for whatever source address the datagram claimed. The captured success
        // response does not pin this, because it carries no USERNAME and is rejected a step later anyway.
        byte[] indication = CapturedRequest;
        indication[1] = 0x11;

        Assert.That(WebRtcDirectStun.IsStunDatagram(indication), Is.True);
        Assert.That(WebRtcDirectStun.TryParseBindingRequest(indication, out _, out _), Is.False);
    }

    [Test]
    public void CheckIntegrity_AcceptsOnlyTheServerUfragKey()
    {
        WebRtcDirectStun.TryParseBindingRequest(CapturedRequest, out STUNMessage? request, out _);

        Assert.That(request, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(WebRtcDirectStun.CheckIntegrity(request, ServerUfrag), Is.True);
            Assert.That(WebRtcDirectStun.CheckIntegrity(request, ClientUfrag), Is.False);
            Assert.That(WebRtcDirectStun.CheckIntegrity(request, ClientPassword), Is.False);
            Assert.That(WebRtcDirectStun.CheckIntegrity(request, ServerUfrag + "x"), Is.False);
            Assert.That(WebRtcDirectStun.CheckIntegrity(request, string.Empty), Is.False);
        });
    }

    [Test]
    public void BuildBindingSuccessResponse_ReproducesTheCapturedResponseByteForByte()
    {
        WebRtcDirectStun.TryParseBindingRequest(CapturedRequest, out STUNMessage? request, out _);
        Assert.That(request, Is.Not.Null);

        byte[] response = WebRtcDirectStun.BuildBindingSuccessResponse(request, CapturedSource, ServerUfrag);

        Assert.That(Convert.ToHexString(response), Is.EqualTo(CapturedResponseHex));
    }

    [Test]
    public void BuildBindingSuccessResponse_NormalizesAMappedSourceBeforeReflectingIt()
    {
        WebRtcDirectStun.TryParseBindingRequest(CapturedRequest, out STUNMessage? request, out _);
        Assert.That(request, Is.Not.Null);

        IPEndPoint mapped = new(IPAddress.Loopback.MapToIPv6(), CapturedSource.Port);
        byte[] response = WebRtcDirectStun.BuildBindingSuccessResponse(request, mapped, ServerUfrag);

        Assert.That(Convert.ToHexString(response), Is.EqualTo(CapturedResponseHex));
    }

    [Test]
    public void Normalize_UnwrapsAnIPv4MappedAddress()
    {
        IPEndPoint normalized = WebRtcDirectStun.Normalize(new(IPAddress.Parse("::ffff:1.2.3.4"), 1234));

        Assert.Multiple(() =>
        {
            Assert.That(normalized.Address, Is.EqualTo(IPAddress.Parse("1.2.3.4")));
            Assert.That(normalized.Port, Is.EqualTo(1234));
        });
    }

    [Test]
    public void Normalize_LeavesARealIPv6AddressAlone()
    {
        IPAddress address = IPAddress.Parse("2001:db8::1");

        IPEndPoint normalized = WebRtcDirectStun.Normalize(new(address, 1234));

        Assert.Multiple(() =>
        {
            Assert.That(normalized.Address, Is.EqualTo(address));
            Assert.That(normalized.Port, Is.EqualTo(1234));
        });
    }

    [Test]
    public void Normalize_DropsTheScopeIdSoTheSamePeerIsOneKey()
    {
        IPAddress scoped = IPAddress.Parse("fe80::1%7");
        Assume.That(scoped.ScopeId, Is.EqualTo(7));

        IPEndPoint normalized = WebRtcDirectStun.Normalize(new(scoped, 1234));

        Assert.Multiple(() =>
        {
            Assert.That(normalized.Address.ScopeId, Is.Zero);
            Assert.That(normalized.Address, Is.EqualTo(IPAddress.Parse("fe80::1")));
        });
    }

    [Test]
    public void Normalize_ReturnsAFreshEndPointBecauseSocketReceivesMutateTheirs()
    {
        IPEndPoint source = new(IPAddress.Loopback, 1234);

        Assert.That(WebRtcDirectStun.Normalize(source), Is.Not.SameAs(source));
    }

    [Test]
    public void IsDtlsRecord_MatchesTheRfc7983Range()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WebRtcDirectStun.IsDtlsRecord([]), Is.False);
            Assert.That(WebRtcDirectStun.IsDtlsRecord([19]), Is.False);
            Assert.That(WebRtcDirectStun.IsDtlsRecord([20]), Is.True);
            Assert.That(WebRtcDirectStun.IsDtlsRecord([63]), Is.True);
            Assert.That(WebRtcDirectStun.IsDtlsRecord([64]), Is.False);
            Assert.That(WebRtcDirectStun.IsDtlsRecord(CapturedRequest), Is.False);
        });
    }
}
