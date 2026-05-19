// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.AutoTls.Internal;
using Google.Protobuf;
using System.Text;

namespace Nethermind.Libp2p.Protocols.AutoTls.Tests;

public class PeerIdAuthTests
{
    [Test]
    public void BuildAuthorizationHeaderSignsPeerIdAuthPayloadPerSpec()
    {
        Identity client = new();
        Identity server = new();
        string hostname = "registration.libp2p.direct";
        string challengeClient = Base64UrlEncode(Enumerable.Repeat((byte)0x11, 32).ToArray());
        string serverPublicKey = Base64UrlEncode(server.PublicKey.ToByteArray());

        string header = PeerIdAuth.BuildAuthorizationHeader(client, hostname, challengeClient, serverPublicKey, "opaque-token");
        IReadOnlyDictionary<string, string> values = PeerIdAuth.ParseChallenge(header);

        byte[] signature = Base64UrlDecode(values["sig"]);
        byte[] expectedPayload = BuildExpectedPayload(hostname, challengeClient, server.PublicKey.ToByteArray());

        Assert.That(client.VerifySignature(expectedPayload, signature), Is.True);
        Assert.That(values["public-key"], Is.EqualTo(Base64UrlEncode(client.PublicKey.ToByteArray())));
        Assert.That(values["challenge-server"], Has.Length.GreaterThanOrEqualTo(32));
        Assert.That(values["opaque"], Is.EqualTo("opaque-token"));
        Assert.That(header, Does.Not.Contain("+"));
        Assert.That(header, Does.Not.Contain("/"));
    }

    private static byte[] BuildExpectedPayload(string hostname, string challengeClient, byte[] serverPublicKey)
    {
        (string Key, byte[] Value)[] parts =
        {
            ("challenge-client", Encoding.UTF8.GetBytes(challengeClient)),
            ("hostname", Encoding.UTF8.GetBytes(hostname)),
            ("server-public-key", serverPublicKey),
        };
        Array.Sort(parts, static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        using MemoryStream ms = new();
        ms.Write(Encoding.ASCII.GetBytes("libp2p-PeerID"));
        foreach ((string key, byte[] value) in parts)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            WriteVarInt(ms, keyBytes.Length + 1 + value.Length);
            ms.Write(keyBytes);
            ms.WriteByte((byte)'=');
            ms.Write(value);
        }
        return ms.ToArray();
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[VarInt.GetSizeInBytes(value)];
        int offset = 0;
        VarInt.Encode(value, buffer, ref offset);
        stream.Write(buffer[..offset]);
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
        => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/'));
}
