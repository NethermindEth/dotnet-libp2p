namespace Multiformats.Address.Protocols
{
    public class P2PWebSocketStar : MultiaddressProtocol
    {
        public P2PWebSocketStar()
            : base("p2p-websocket-star", 479, 0)
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
