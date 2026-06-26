// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Text;

namespace Nethermind.Libp2p.Protocols.I2p;

public sealed record I2pDatagram(string? SourceDestination, byte[] Payload, IReadOnlyDictionary<string, string> Options)
{
    private static readonly Encoding SamEncoding = Encoding.ASCII;

    public static I2pDatagram ParseForwarded(ReadOnlyMemory<byte> packet)
    {
        ReadOnlySpan<byte> span = packet.Span;
        int lineEnd = span.IndexOf((byte)'\n');
        if (lineEnd < 0)
        {
            throw new I2pException("SAM datagram did not contain a destination header.");
        }

        ReadOnlySpan<byte> headerBytes = span[..lineEnd];
        if (headerBytes.Length > 0 && headerBytes[^1] == '\r')
        {
            headerBytes = headerBytes[..^1];
        }

        string header = SamEncoding.GetString(headerBytes);
        string[] tokens = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            throw new I2pException("SAM datagram destination header is empty.");
        }

        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < tokens.Length; i++)
        {
            int separator = tokens[i].IndexOf('=');
            if (separator <= 0)
            {
                throw new I2pException($"Invalid SAM datagram option token: {tokens[i]}");
            }

            options[tokens[i][..separator]] = tokens[i][(separator + 1)..];
        }

        return new I2pDatagram(tokens[0], span[(lineEnd + 1)..].ToArray(), options);
    }
}
