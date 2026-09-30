namespace Nethermind.Libp2p.Core.Tests;

public class VarintTests
{
    [Test]
    public async Task Test_VarInt_Assumption_And_Roundtrip_Encoding()
    {
        Assert.That(VarInt.GetSizeInBytes(System.Int32.MinValue), Is.EqualTo(5));
        Assert.That(VarInt.GetSizeInBytes(System.UInt64.MinValue), Is.EqualTo(1));
        Assert.That(VarInt.GetSizeInBytes(System.Int32.MaxValue), Is.EqualTo(5));
        Assert.That(VarInt.GetSizeInBytes(System.UInt64.MaxValue), Is.EqualTo(10));
        Assert.That(VarInt.GetSizeInBytes(1UL), Is.EqualTo(1));
        Assert.That(VarInt.GetSizeInBytes(0UL), Is.EqualTo(1));
        Assert.That(VarInt.GetSizeInBytes(1), Is.EqualTo(1));
        Assert.That(VarInt.GetSizeInBytes(0), Is.EqualTo(1));
        Span<byte> memoryA = stackalloc byte[10];
        int offset = 0;
        VarInt.Encode(System.UInt64.MaxValue, memoryA, ref offset);
        offset = 0;
        Assert.That(VarInt.Decode(memoryA, ref offset), Is.EqualTo(System.UInt64.MaxValue));
        offset = 0;
        memoryA.Clear();

        VarInt.Encode(System.UInt64.MinValue, memoryA, ref offset);
        offset = 0;
        Assert.That(VarInt.Decode(memoryA, ref offset), Is.EqualTo(System.UInt64.MinValue));
        offset = 0;
        memoryA.Clear();

        System.UInt64 roundtrip_ulong_target = (ulong)System.Random.Shared.NextInt64();
        VarInt.Encode(roundtrip_ulong_target, memoryA, ref offset);
        offset = 0;
        Assert.That(roundtrip_ulong_target, Is.EqualTo(VarInt.Decode(memoryA, ref offset)));
        offset = 0;
        memoryA.Clear();

        Span<byte> memoryB = stackalloc byte[5];
        VarInt.Encode(System.Int32.MaxValue, memoryB, ref offset);
        offset = 0;
        Assert.That(VarInt.Decode(memoryB, ref offset), Is.EqualTo(System.Int32.MaxValue));
        offset = 0;
        memoryB.Clear();

        VarInt.Encode(System.Int32.MinValue, memoryB, ref offset);
        offset = 0;
        Assert.That((int)VarInt.Decode(memoryB, ref offset), Is.EqualTo(System.Int32.MinValue));
        offset = 0;
        memoryB.Clear();

        System.Int32 roundtrip_int_target = System.Random.Shared.Next();
        VarInt.Encode(roundtrip_int_target, memoryB, ref offset);
        offset = 0;
        Assert.That(roundtrip_int_target, Is.EqualTo((int)VarInt.Decode(memoryB, ref offset)));
        offset = 0;
        memoryB.Clear();
    }
}
