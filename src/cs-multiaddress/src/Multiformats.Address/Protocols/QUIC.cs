namespace Multiformats.Address.Protocols
{
    public class QUIC : MultiaddressProtocol
    {
        public QUIC()
            : base("quic", 460, 0)
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
