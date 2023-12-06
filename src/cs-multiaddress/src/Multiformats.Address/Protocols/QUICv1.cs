using System;
using System.Collections.Generic;
using System.Text;

namespace Multiformats.Address.Protocols
{
     public class QUICv1 : MultiaddressProtocol
    {
        public QUICv1()
            : base("quic-v1", 461, 0)
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
