using System;

namespace Multiformats.Address.Protocols
{
    public class WebSocket : MultiaddressProtocol
    {
        public WebSocket()
            : base("ws", 477, 0)
        {
        }

        public override void Decode(byte[] bytes)
        {
        }

        public override void Decode(string value)
        {
        }

        public override byte[] ToBytes() => EmptyBuffer;
    }
}
