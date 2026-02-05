// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

using System.Runtime.CompilerServices;
public static class VarInt
{
    //(ulong)((uint) number) casts number to uint first then ulong because apparently I had to
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(int number, Span<byte> buf, ref int offset) =>
        Encode((ulong)((uint)number), buf, ref offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encode(ulong number, Span<byte> buf, ref int offset)
    {
        int byte_number = GetSizeInBytes(number);
        for (int i = 0; i < byte_number; i++)
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

    //(ulong)((uint) number) casts number to uint first then ulong because apparently I had to
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSizeInBytes(int number) =>
        GetSizeInBytes((ulong)((uint)number));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetSizeInBytes(ulong number)
    {
        int bit_number = System.Numerics.BitOperations.Log2(number) + 1;
        return (bit_number + 6) / 7;
    }
    /// <summary>
    /// Decode either the encoded 32bit integer or 64bit unsigned integer to unsigned integer 64bit
    /// </summary>
    /// <param name="line"></param>
    /// <param name="offset"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Decode(Span<byte> source, ref int offset)
    {
        ulong result = 0;
        int shift = 0;
        int bytesRead = 0;
        //search for a range no larger than the span and no larger than 10 bytes passed offset (encoding support almost System.UInt64 which encodes to 10 bytes maximum)
        while (bytesRead < source.Length)
        {
            byte @byte = source[offset + bytesRead++];
            // Use the AND operator (& 0x7F) to get the 7 bits of data
            // Use the OR operator (|=) to add them to the result
            result |= ((ulong)(@byte & 0x7F)) << shift;

            // Check the 8th bit: If it is 0, return the result
            if ((@byte & 0x80) == 0)
            {
                offset = offset + bytesRead;
                return result;
            }
            shift += 7;
            // Safety check: a 64-bit int can't be more than 10 bytes (10 * 7 = 70)
            if (shift >= 70) throw new FormatException("Invalid 7-bit encoding");
        }
        throw new EndOfStreamException("Exhausted span before end of integer.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task<ulong> DecodeUlong(IReader buf)
    {
        ulong res = 0;
        byte mul = 0;
        for (int i = 0; i < 10; i++)
        {
            byte @byte = (await buf.ReadAsync(1).OrThrow()).FirstSpan[0];
            res += ((ulong)@byte & 127) << mul;
            mul += 7;
            if ((@byte & 128) == 0)
            {
                return res;
            }
        }
        throw new FormatException("Invalid 7-bit encoding");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task<int> Decode(IReader buf, CancellationToken token = default)
    {
        int res = 0;
        byte mul = 0;
        for (int i = 0; i < 5; i++)
        {
            byte @byte = (await buf.ReadAsync(1, token: token).OrThrow()).FirstSpan[0];
            res += (@byte & 127) << mul;
            mul += 7;
            if ((@byte & 128) == 0)
            {
                return res;
            }
        }
        throw new FormatException("Invalid 7-bit encoding");
    }
}

