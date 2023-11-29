namespace Multiformats.Address.Protocols
{
    public class TCP : Number
    {
        public TCP()
            : base("tcp", 6)
        {
        }

        public TCP(ushort port)
            : this()
        {
            Value = port;
        }
    }
}
