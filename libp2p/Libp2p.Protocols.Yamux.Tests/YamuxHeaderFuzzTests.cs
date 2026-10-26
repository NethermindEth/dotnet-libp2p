// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Yamux;

namespace Nethermind.Libp2p.Protocols.Yamux.Tests;

/// <summary>
/// Deterministic property fuzzer for <see cref="YamuxHeader"/> decode/encode.
/// Covers arbitrary, negative and huge <c>Length</c> values, unknown
/// <c>Type</c> values and unknown flag bits. Committed seeds make every run
/// reproducible; no new dependencies.
/// </summary>
[TestFixture]
public class YamuxHeaderFuzzTests
{
    public static IEnumerable<int> Seeds()
    {
        for (int seed = 1001; seed <= 1010; seed++)
        {
            yield return seed;
        }
    }

    [TestCaseSource(nameof(Seeds))]
    public void Header_RoundTrip_PreservesEveryField(int seed)
    {
        Random rng = new(seed);
        for (int i = 0; i < 2000; i++)
        {
            YamuxHeader original = new()
            {
                Version = (byte)rng.Next(256),
                Type = (YamuxHeaderType)rng.Next(256),
                Flags = (YamuxHeaderFlags)rng.Next(short.MinValue, short.MaxValue + 1),
                StreamID = NextInt32(rng),
                Length = NextInt32(rng)
            };
            byte[] bytes = new byte[12];
            YamuxHeader copy = original;
            YamuxHeader.ToBytes(bytes, ref copy);
            YamuxHeader decoded = YamuxHeader.FromBytes(bytes);
            Assert.That(decoded.Version, Is.EqualTo(original.Version), $"seed {seed} iter {i}");
            Assert.That(decoded.Type, Is.EqualTo(original.Type), $"seed {seed} iter {i}");
            Assert.That(decoded.Flags, Is.EqualTo(original.Flags), $"seed {seed} iter {i}");
            Assert.That(decoded.StreamID, Is.EqualTo(original.StreamID), $"seed {seed} iter {i}");
            Assert.That(decoded.Length, Is.EqualTo(original.Length), $"seed {seed} iter {i}");
        }
    }

    [Test]
    public void Header_Flags_RoundTrip_Exhaustive()
    {
        // All 65536 flag patterns (including unknown bits and the sign bit) must survive.
        for (int flags = short.MinValue; flags <= short.MaxValue; flags++)
        {
            YamuxHeader original = new()
            {
                Version = 0,
                Type = YamuxHeaderType.Data,
                Flags = (YamuxHeaderFlags)flags,
                StreamID = 3,
                Length = 5
            };
            byte[] bytes = new byte[12];
            YamuxHeader copy = original;
            YamuxHeader.ToBytes(bytes, ref copy);
            Assert.That(YamuxHeader.FromBytes(bytes).Flags, Is.EqualTo(original.Flags));
        }
    }

    [TestCaseSource(nameof(Seeds))]
    public void Header_FromBytes_NeverThrowsOnTwelveBytes(int seed)
    {
        Random rng = new(seed);
        for (int i = 0; i < 2000; i++)
        {
            byte[] bytes = new byte[12];
            rng.NextBytes(bytes);
            Assert.DoesNotThrow(() => YamuxHeader.FromBytes(bytes), $"seed {seed} iter {i}");
        }
    }

    [TestCaseSource(nameof(Seeds))]
    public void Header_Decode_MatchesManualBigEndianParse(int seed)
    {
        Random rng = new(seed);
        for (int i = 0; i < 500; i++)
        {
            byte[] bytes = new byte[12];
            rng.NextBytes(bytes);
            YamuxHeader decoded = YamuxHeader.FromBytes(bytes);
            Assert.That(decoded.Version, Is.EqualTo(bytes[0]));
            Assert.That((byte)decoded.Type, Is.EqualTo(bytes[1]));
            Assert.That((short)decoded.Flags, Is.EqualTo(BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(2))));
            Assert.That(decoded.StreamID, Is.EqualTo(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4))));
            Assert.That(decoded.Length, Is.EqualTo(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8))));
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(11)]
    public void Header_FromBytes_ShortSpan_Throws(int length)
    {
        // Documents the precondition: callers (ReadHeaderAsync) must read 12 bytes first.
        // The exact exception type (ArgumentOutOfRangeException from the range operator)
        // is incidental; what matters is that it fails loudly instead of decoding garbage.
        Assert.That(() => YamuxHeader.FromBytes(new byte[length]), Throws.Exception);
    }

    [TestCase(0)]
    [TestCase(11)]
    public void Header_ToBytes_ShortSpan_Throws(int length)
    {
        // Same precondition as FromBytes. Note the incidental inconsistency: an empty span
        // throws IndexOutOfRangeException (indexer) while longer short spans throw
        // ArgumentOutOfRangeException (BinaryPrimitives). Both fail loudly; the protocol
        // always passes exactly 12 bytes, so this is characterization, not a bug.
        YamuxHeader header = new();
        Assert.That(() => YamuxHeader.ToBytes(new byte[length], ref header), Throws.Exception);
    }

    private static int NextInt32(Random rng)
    {
        int[] interesting = [0, 1, -1, 255, 256, -256, 262144, 16777216, int.MinValue, int.MaxValue, int.MaxValue - 1];
        return rng.Next(4) == 0 ? interesting[rng.Next(interesting.Length)] : rng.Next(int.MinValue, int.MaxValue);
    }
}
