namespace Multiformats.Address.Protocols
{
    public class DCCP : Number
    {
        public DCCP()
            : base("dccp", 33)
        {
        }

        public DCCP(int port)
            : this()
        {
            Value = port;
        }
    }
}
