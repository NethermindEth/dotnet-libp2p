// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Numerics;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.AutoTls.Internal;

/// <summary>
/// Encodes a <see cref="PeerId"/> as a CIDv1 / libp2p-key multibase string in
/// <c>base36 lower</c>. This is the encoding the p2p-forge / AutoTLS ecosystem
/// uses for the <c>&lt;peerId&gt;.libp2p.direct</c> subdomain — the default
/// <see cref="PeerId.ToString"/> produces base58btc and the built-in
/// <see cref="PeerId.ToCidString"/> uses base32, neither of which matches.
/// </summary>
internal static class PeerIdEncoding
{
    // libp2p-key multicodec, per https://github.com/multiformats/multicodec
    private const int Libp2pKeyCodec = 0x72;
    private const byte CidV1 = 0x01;
    private const char Base36LowerPrefix = 'k';
    private const string Base36LowerAlphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    public static string ToBase36CidString(PeerId peerId)
    {
        ArgumentNullException.ThrowIfNull(peerId);

        // CID payload: <cid-version><multicodec-varint><multihash-bytes>
        int codecSize = VarInt.GetSizeInBytes(Libp2pKeyCodec);
        byte[] cidBytes = new byte[1 + codecSize + peerId.Bytes.Length];
        int offset = 0;
        cidBytes[offset++] = CidV1;
        VarInt.Encode(Libp2pKeyCodec, cidBytes, ref offset);
        Array.Copy(peerId.Bytes, 0, cidBytes, offset, peerId.Bytes.Length);

        return Base36LowerPrefix + EncodeBase36Lower(cidBytes);
    }

    private static string EncodeBase36Lower(ReadOnlySpan<byte> bytes)
    {
        // Each leading zero byte becomes a leading '0' in the encoded output.
        int leadingZeros = 0;
        while (leadingZeros < bytes.Length && bytes[leadingZeros] == 0)
        {
            leadingZeros++;
        }

        // Prepend a 0x00 sign byte to keep the BigInteger positive, and
        // reverse to little-endian as expected by the BigInteger constructor.
        byte[] tmp = new byte[bytes.Length + 1];
        for (int i = 0; i < bytes.Length; i++)
        {
            tmp[i] = bytes[bytes.Length - 1 - i];
        }
        tmp[bytes.Length] = 0;

        BigInteger n = new(tmp);
        if (n.IsZero)
        {
            return new string('0', Math.Max(leadingZeros, 1));
        }

        Span<char> buffer = stackalloc char[bytes.Length * 2 + 8]; // upper bound
        int pos = buffer.Length;
        while (n > 0)
        {
            n = BigInteger.DivRem(n, 36, out BigInteger rem);
            buffer[--pos] = Base36LowerAlphabet[(int)rem];
        }

        return new string('0', leadingZeros) + new string(buffer[pos..]);
    }
}
