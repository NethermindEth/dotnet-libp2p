namespace Nethermind.Libp2p.Core.Tests;
public class VarintTests
{
   [Test]
   public async Task Test_VarInt_Assumption_And_Roundtrip_Encoding()
   {
      Assert.That(5,  Is.EqualTo(VarInt.GetSizeInBytes(System.Int32.MinValue)));
      Assert.That(1,  Is.EqualTo(VarInt.GetSizeInBytes(System.UInt64.MinValue)));
      Assert.That(5,  Is.EqualTo(VarInt.GetSizeInBytes(System.Int32.MaxValue)));
      Assert.That(10, Is.EqualTo(VarInt.GetSizeInBytes(System.UInt64.MaxValue)));
      Assert.That(1, Is.EqualTo(VarInt.GetSizeInBytes(1UL)));
      Assert.That(1, Is.EqualTo(VarInt.GetSizeInBytes(0UL)));
      Assert.That(1, Is.EqualTo(VarInt.GetSizeInBytes(1)));
      Assert.That(1, Is.EqualTo(VarInt.GetSizeInBytes(0)));
      Span<byte> memoryA = stackalloc byte[10];
      int offset = 0;
      VarInt.Encode(System.UInt64.MaxValue, memoryA, ref offset);
      offset = 0;
      Assert.That(System.UInt64.MaxValue, Is.EqualTo(VarInt.Decode(memoryA, ref offset)));
      offset = 0;
      memoryA.Clear();

      VarInt.Encode(System.UInt64.MinValue, memoryA, ref offset);
      offset = 0;
      Assert.That(System.UInt64.MinValue, Is.EqualTo(VarInt.Decode(memoryA, ref offset)));
      offset = 0;
      memoryA.Clear();

      System.UInt64 roundtrip_ulong_target = (ulong) System.Random.Shared.NextInt64();
      VarInt.Encode(roundtrip_ulong_target, memoryA, ref offset);
      offset = 0;
      Assert.That(roundtrip_ulong_target, Is.EqualTo(VarInt.Decode(memoryA, ref offset)));
      offset = 0;
      memoryA.Clear();

      Span<byte> memoryB = stackalloc byte[5];
      VarInt.Encode(System.Int32.MaxValue, memoryB, ref offset);
      offset = 0;
      Assert.That(System.Int32.MaxValue, Is.EqualTo(VarInt.Decode(memoryB, ref offset)));
      offset = 0;
      memoryB.Clear();

      VarInt.Encode(System.Int32.MinValue, memoryB, ref offset);
      offset = 0;
      Assert.That(System.Int32.MinValue, Is.EqualTo((int) VarInt.Decode(memoryB, ref offset)));
      offset = 0;
      memoryB.Clear();

      System.Int32 roundtrip_int_target = System.Random.Shared.Next();
      VarInt.Encode(roundtrip_int_target, memoryB, ref offset);
      offset = 0;
      Assert.That(roundtrip_int_target, Is.EqualTo((int) VarInt.Decode(memoryB, ref offset)));
      offset = 0;
      memoryB.Clear();
   }
}