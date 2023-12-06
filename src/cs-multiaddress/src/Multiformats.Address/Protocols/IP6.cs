using System;
using System.Net;
using System.Net.Sockets;

namespace Multiformats.Address.Protocols
{
    public class IP6 : IP
    {
        public IP6()
            : base("ip6", 41, 128)
        {
        }

        public IP6(IPAddress address)
            : this()
        {
            if (address.AddressFamily != AddressFamily.InterNetworkV6)
                throw new Exception("Address is not IPv6");

            Value = address;
        }

        public override void Decode(string value)
        {
            base.Decode(value);

            if (Value != null && ((IPAddress)Value).AddressFamily != AddressFamily.InterNetworkV6)
                throw new Exception("Address is not IPv6");
        }

        public override void Decode(byte[] bytes)
        {
            base.Decode(bytes);

            if (Value != null && ((IPAddress)Value).AddressFamily != AddressFamily.InterNetworkV6)
                throw new Exception("Address is not IPv6");
        }
    }
}
