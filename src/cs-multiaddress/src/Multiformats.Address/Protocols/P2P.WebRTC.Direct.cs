namespace Multiformats.Address.Protocols
{
    public class P2PWebRTCDirect : MultiaddressProtocol
    {
        public P2PWebRTCDirect()
            : base("p2p-webrtc-direct", 276, 0)
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
