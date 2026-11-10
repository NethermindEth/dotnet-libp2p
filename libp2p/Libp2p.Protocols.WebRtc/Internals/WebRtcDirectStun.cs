// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SIPSorcery.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// The listener's trust boundary: a webrtc-direct listener multiplexes STUN and DTLS on one UDP port,
/// so every datagram from the internet is classified and parsed here before anything else sees it.
/// Everything is pure — no sockets, no state — so the decisions it makes are testable against captured
/// wire bytes rather than against a live stack.
/// </summary>
internal static class WebRtcDirectStun
{
    /// <summary>
    /// RFC 7983 demultiplexing, plus RFC 5389's magic cookie. The cookie check is not redundant with the
    /// two-zero-bits check: SIPSorcery's parser ignores the cookie entirely, so without it twenty zero
    /// bytes would be accepted as a STUN message.
    /// </summary>
    public static bool IsStunDatagram(ReadOnlySpan<byte> datagram) =>
        datagram.Length >= STUNHeader.STUN_HEADER_LENGTH &&
        (datagram[0] & STUNHeader.STUN_INITIAL_BYTE_MASK) == 0 &&
        BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]) == STUNHeader.MAGIC_COOKIE;

    /// <summary>RFC 7983: the 20..63 first-byte range is reserved for DTLS on a multiplexed port.</summary>
    public static bool IsDtlsRecord(ReadOnlySpan<byte> datagram) =>
        datagram.Length > 0 && datagram[0] is >= 20 and <= 63;

    /// <summary>
    /// Produces a fresh endpoint that is safe to use as a dictionary key and in XOR-MAPPED-ADDRESS.
    /// Fresh because a socket receive mutates the <see cref="EndPoint"/> it is handed, so the instance a
    /// read loop reports is not one that can be stored.
    /// <para>
    /// A dual-mode socket delivers a v4 peer as <c>::ffff:a.b.c.d</c>, which would make the same peer two
    /// different keys depending on the socket it arrived on and would make an "is this peer IPv6" test
    /// lie. The scope id goes for the same reason: it participates in <see cref="IPAddress"/> equality and
    /// hashing, and it describes our local interface rather than the peer.
    /// </para>
    /// </summary>
    public static IPEndPoint Normalize(IPEndPoint endPoint)
    {
        IPAddress address = endPoint.Address;

        if (address.IsIPv4MappedToIPv6)
        {
            return new(address.MapToIPv4(), endPoint.Port);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            return new(new IPAddress(address.GetAddressBytes()), endPoint.Port);
        }

        return new(address, endPoint.Port);
    }

    /// <summary>
    /// Parses a datagram as a libp2p webrtc-direct STUN binding request, recovering the ICE credentials
    /// from its USERNAME. Returns false for anything else.
    /// <para>
    /// This method must not throw for ANY input, and that is not free. SIPSorcery's parser indexes the
    /// buffer before it has finished length-checking it, so a datagram of 1 to 19 bytes leaves the header
    /// null and then dereferences it. The length-and-cookie guard, not the catch, is what stops that, which
    /// is why <see cref="IsStunDatagram"/> has to run before the parse rather than after it.
    /// </para>
    /// <para>
    /// The catch behind the guard is belt and braces, not cover for a known throw: against 10.0.14 no
    /// datagram of 20 bytes or more carrying a valid cookie was found to throw, and an unrecognized message
    /// or attribute type code comes back as its raw numeric value rather than as an exception. It stays
    /// because the parser is a dependency we do not control and it already gets one length check wrong, and
    /// an unauthenticated datagram off the internet is not where we want to discover the next one.
    /// </para>
    /// </summary>
    public static bool TryParseBindingRequest(
        byte[] datagram,
        [NotNullWhen(true)] out STUNMessage? request,
        out WebRtcIceCredentials credentials)
    {
        request = null;
        credentials = default;

        if (!IsStunDatagram(datagram))
        {
            return false;
        }

        STUNMessage? parsed;
        try
        {
            parsed = STUNMessage.ParseSTUNMessage(datagram, datagram.Length);
        }
        catch (Exception)
        {
            return false;
        }

        if (parsed?.Header is not { MessageType: STUNMessageTypesEnum.BindingRequest })
        {
            return false;
        }

        byte[]? username = parsed.GetFirstAttribute(STUNAttributeTypesEnum.Username)?.Value;
        if (username is null ||
            !WebRtcIceCredentials.TryParseStunUsername(Encoding.UTF8.GetString(username), out credentials))
        {
            return false;
        }

        request = parsed;
        return true;
    }

    /// <summary>
    /// Verifies the request's MESSAGE-INTEGRITY against an HMAC-SHA1 key of the UTF-8 bytes of
    /// <paramref name="serverUfrag"/>, as webrtc-direct specifies.
    /// <para>
    /// A true result AUTHORIZES NOTHING. The key is the server ufrag, which is the first half of the
    /// USERNAME carried in the very same packet, so every value the check needs is already on the wire in
    /// front of whoever is reading it. It therefore proves only that the sender saw a request — it rules
    /// out blind, off-path spoofing and nothing more. No caller may let a passing check evict a peer,
    /// re-key a session, move an egress address, or allocate state without an independent bound.
    /// </para>
    /// </summary>
    /// <summary>
    /// Whether this binding request nominates its candidate pair (RFC 8445 section 7.3.2's
    /// USE-CANDIDATE). An ICE-lite endpoint has no agent of its own, so this flag is the only signal
    /// it gets about which of a dialer's candidates was actually selected -- and sections 7.3.2 and
    /// 12.1.1 require it to use that pair rather than whichever candidate happened to probe first.
    /// </summary>
    public static bool NominatesCandidate(STUNMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Attributes.Any(attribute => attribute.AttributeType == STUNAttributeTypesEnum.UseCandidate);
    }

    public static bool CheckIntegrity(STUNMessage request, string serverUfrag)
    {
        try
        {
            return request.CheckIntegrity(Encoding.UTF8.GetBytes(serverUfrag));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the signed binding success response: the request's transaction id, XOR-MAPPED-ADDRESS for the
    /// normalized source, MESSAGE-INTEGRITY keyed with the server ufrag and a FINGERPRINT.
    /// </summary>
    public static byte[] BuildBindingSuccessResponse(STUNMessage request, IPEndPoint source, string serverUfrag)
    {
        IPEndPoint reflexive = Normalize(source);

        STUNMessage response = new(STUNMessageTypesEnum.BindingSuccessResponse);
        response.Header.TransactionId = request.Header.TransactionId;
        response.AddXORMappedAddressAttribute(reflexive.Address, reflexive.Port);

        return response.ToByteBufferStringKey(serverUfrag, true);
    }
}
