// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.I2p.Tests;

internal static class I2pTestDestinations
{
    public static byte[] Garlic64Bytes()
    {
        byte[] bytes = new byte[387];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        return bytes;
    }

    public static byte[] Garlic32Bytes()
    {
        byte[] bytes = new byte[32];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        return bytes;
    }

    public static string Garlic64() => new Garlic64(Garlic64Bytes()).Destination;

    public static string Garlic32() => new Garlic32(Garlic32Bytes()).Destination;
}
