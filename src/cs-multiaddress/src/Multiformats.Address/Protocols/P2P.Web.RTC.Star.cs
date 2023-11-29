namespace Multiformats.Address.Protocols
{
    public class P2PWebRTCStar : MultiaddressProtocol
    {
        public P2PWebRTCStar()
            : base("p2p-webrtc-star", 275, 0)
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
