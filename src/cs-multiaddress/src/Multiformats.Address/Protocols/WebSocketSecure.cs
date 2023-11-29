using System;

namespace Multiformats.Address.Protocols
{
    public class WebSocketSecure : MultiaddressProtocol
    {
        public WebSocketSecure()
            : base("wss", 478, 0)
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
