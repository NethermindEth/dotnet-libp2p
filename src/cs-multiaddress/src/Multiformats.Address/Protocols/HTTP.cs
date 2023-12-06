using System;

namespace Multiformats.Address.Protocols
{
    public class HTTP : MultiaddressProtocol
    {
        public HTTP()
            : base("http", 480, 0)
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