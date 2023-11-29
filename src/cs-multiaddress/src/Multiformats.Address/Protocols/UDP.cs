namespace Multiformats.Address.Protocols
{
    public class UDP : Number
    {
        public UDP()
            : base("udp", 17)
        {
        }

        public UDP(ushort port)
            : this()
        {
            Value = port;
        }
    }
}
