namespace Multiformats.Address.Protocols
{
    public class SCTP : Number
    {
        public SCTP()
            : base("sctp", 132)
        {
        }

        public SCTP(int port)
            : this()
        {
            Value = port;
        }
    }
}