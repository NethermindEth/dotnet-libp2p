using System;

namespace Multiformats.Address.Protocols
{
    public class UDT : MultiaddressProtocol
    {
        public UDT()
            : base("udt", 302, 0)
        {
        }

        public override void Decode(string value)
        {
        }

        public override void Decode(byte[] bytes)
        {
        }

        public override byte[] ToBytes() => EmptyBuffer;
    }
}