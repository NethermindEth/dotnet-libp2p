namespace Multiformats.Address.Protocols
{
    public class TCP : Number
    {
        public TCP()
            : base("tcp", 6)
        {
        }

        public TCP(int port)
            : this()
        {
            Value = port;
        }

        public TCP(uint port)
            : this()
        {
            Value = port;
        }

        public TCP(short port)
            : this()
        {
            Value = port;
        }

        public TCP(ushort port)
            : this()
        {
            Value = port;
        }
    }
}
