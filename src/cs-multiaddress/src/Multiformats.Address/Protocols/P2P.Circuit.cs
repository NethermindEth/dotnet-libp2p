namespace Multiformats.Address.Protocols
{
    public class P2PCircuit : MultiaddressProtocol
    {
        public P2PCircuit()
            : base("p2p-circuit", 290, 0)
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
