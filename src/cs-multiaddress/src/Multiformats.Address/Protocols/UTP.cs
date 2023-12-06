using System;

namespace Multiformats.Address.Protocols
{
    public class UTP : MultiaddressProtocol
    {
        public UTP()
            : base("utp", 301, 0)
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