using System;
using System.Net;
using System.Net.Sockets;

namespace Multiformats.Address.Protocols
{
    public class IP4 : IP
    {
        public IP4()
            : base("ip4", 4, 32)
        {
        }

        public IP4(IPAddress address)
            : this()
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
                throw new Exception("Address is not IPv4");

            Value = address;
        }

        public override void Decode(string value)
        {
            base.Decode(value);

            if (Value != null && ((IPAddress)Value).AddressFamily != AddressFamily.InterNetwork)
                throw new Exception("Address is not IPv4");
        }

        public override void Decode(byte[] bytes)
        {
            base.Decode(bytes);

            if (Value != null && ((IPAddress)Value).AddressFamily != AddressFamily.InterNetwork)
                throw new Exception("Address is not IPv4");
        }
    }
}
