// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public static class VarInt
{
    public static void Encode(int number, Span<byte> buf, ref int offset)
    {
        for (int i = 0; i < 9; i++)
        {
            byte newByte = (byte)(number & 127);
            number >>= 7;

            if (number != 0)
            {
                buf[offset + i] = (byte)(newByte | 128);
            }
            else
            {
                buf[offset + i] = newByte;
                offset += i + 1;
                return;
            }
        }
    }

    public static int GetSizeInBytes(int number)
    {
        for (int i = 1; i <= 9; i++)
        {
            number >>= 7;

            if (number == 0)
            {
                return i;
            }
        }

        throw new ArgumentException(nameof(number));
    }

    public static ulong Decode(Span<byte> line, ref int offset)
    {
        ulong res = 0;
        for (int i = 0; i < 9; i++)
        {
            if ((line[offset + i] & 128) == 0)
            {
                for (int j = offset + i; j >= offset; j--)
                {
                    res <<= 7;
                    res |= (ulong)line[j] & 127;
                }

                offset += i + 1;
                return res;
            }
        }

        return 0;
    }

    public static async Task<ulong> DecodeUlong(IReader buf)
    {
        ulong res = 0;
        byte mul = 0;
        for (int i = 0; i < 9; i++)
        {
            byte @byte = (await buf.ReadAsync(1)).FirstSpan[0];
            res += ((ulong)@byte & 127) << mul;
            mul += 7;
            if ((@byte & 128) == 0)
            {
                return res;
            }
        }
    
        return 0;
    }

    public static async Task<int> Decode(IReader buf)
    {
        int res = 0;
        byte mul = 0;
        for (int i = 0; i < 9; i++)
        {
            byte @byte = (await buf.ReadAsync(1)).FirstSpan[0];
            res += ((int)@byte & 127) << mul;
            mul += 7;
            if ((@byte & 128) == 0)
            {
                return res;
            }
        }
    
        return 0;
    }
}
