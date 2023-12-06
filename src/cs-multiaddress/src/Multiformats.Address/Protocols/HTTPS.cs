using System;

namespace Multiformats.Address.Protocols
{
    public class HTTPS : MultiaddressProtocol
    {
        public HTTPS()
            : base("https", 480, 0)
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