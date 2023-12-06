using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Multiformats.Address.Net;
using Xunit;

namespace Multiformats.Address.Tests
{
    public class ConvertTests
    {
        [Fact]
        public void IPEndPoint_GivenIPv4Tcp_ReturnsValid()
        {
            var ep = new IPEndPoint(IPAddress.Loopback, 1337);
            var ma = ep.ToMultiaddress(ProtocolType.Tcp);
            var result = ma.ToString();

            Assert.Equal("/ip4/127.0.0.1/tcp/1337", result);
        }
        
        [Fact]
        public void IPEndPoint_GivenIPv6Tcp_ReturnsValid()
        {
            var ep = new IPEndPoint(IPAddress.IPv6Loopback, 1337);
            var ma = ep.ToMultiaddress(ProtocolType.Tcp);
            var result = ma.ToString();

            Assert.Equal("/ip6/::1/tcp/1337", result);
        }

        [Fact]
        public void IPEndPoint_GivenIPv4Udp_ReturnsValid()
        {
            var ep = new IPEndPoint(IPAddress.Loopback, 1337);
            var ma = ep.ToMultiaddress(ProtocolType.Udp);
            var result = ma.ToString();

            Assert.Equal("/ip4/127.0.0.1/udp/1337", result);
        }

        [Fact]
        public void IPEndPoint_GivenIPv6Udp_ReturnsValid()
        {
            var ep = new IPEndPoint(IPAddress.IPv6Loopback, 1337);
            var ma = ep.ToMultiaddress(ProtocolType.Udp);
            var result = ma.ToString();

            Assert.Equal("/ip6/::1/udp/1337", result);
        }

        [Fact]
        public void Multiaddress_GivenIPv4Tcp_ReturnsValidEndPoint()
        {
            var ma = Multiaddress.Decode("/ip4/127.0.0.1/tcp/1337");
            ProtocolType p;
            var ep = ma.ToEndPoint(out p);

            Assert.Equal(AddressFamily.InterNetwork, ep.AddressFamily);
            Assert.Equal(IPAddress.Loopback, ep.Address);
            Assert.Equal(1337, ep.Port);
            Assert.Equal(ProtocolType.Tcp, p);
        }

        [Fact]
        public void Multiaddress_GivenIPv4Udp_ReturnsValidEndPoint()
        {
            var ma = Multiaddress.Decode("/ip4/127.0.0.1/udp/1337");
            ProtocolType p;
            var ep = ma.ToEndPoint(out p);

            Assert.Equal(AddressFamily.InterNetwork, ep.AddressFamily);
            Assert.Equal(IPAddress.Loopback, ep.Address);
            Assert.Equal(1337, ep.Port);
            Assert.Equal(ProtocolType.Udp, p);
        }

        [Fact]
        public void Multiaddress_GivenIPv6Tcp_ReturnsValidEndPoint()
        {
            var ma = Multiaddress.Decode("/ip6/::1/tcp/1337");
            ProtocolType p;
            var ep = ma.ToEndPoint(out p);

            Assert.Equal(AddressFamily.InterNetworkV6, ep.AddressFamily);
            Assert.Equal(IPAddress.IPv6Loopback, ep.Address);
            Assert.Equal(1337, ep.Port);
            Assert.Equal(ProtocolType.Tcp, p);
        }

        [Fact]
        public void Multiaddress_GivenIPv6Udp_ReturnsValidEndPoint()
        {
            var ma = Multiaddress.Decode("/ip6/::1/udp/1337");
            ProtocolType p;
            var ep = ma.ToEndPoint(out p);

            Assert.Equal(AddressFamily.InterNetworkV6, ep.AddressFamily);
            Assert.Equal(IPAddress.IPv6Loopback, ep.Address);
            Assert.Equal(1337, ep.Port);
            Assert.Equal(ProtocolType.Udp, p);
        }

        [Fact]
        public void Socket_GivenMultiaddress_CreatesSocket()
        {
            var ma = Multiaddress.Decode("/ip4/127.0.0.1/tcp/1337");
            using (var socket = ma.CreateSocket())
            {
                Assert.Equal(AddressFamily.InterNetwork, socket.AddressFamily);
                Assert.Equal(ProtocolType.Tcp, socket.ProtocolType);
                Assert.Equal(SocketType.Stream, socket.SocketType);
            }
        }

        [Theory]
        [InlineData("/ip4/127.0.0.1/udp/1234", true)]
        [InlineData("/ip4/127.0.0.1/tcp/1234", true)]
        [InlineData("/ip4/127.0.0.1/udp/1234/tcp/1234", true)]
        [InlineData("/ip4/127.0.0.1/tcp/12345/ip4/1.2.3.4", true)]
        [InlineData("/ip6/::1/tcp/80", true)]
        [InlineData("/ip6/::1/udp/80", true)]
        [InlineData("/ip6/::1", true)]
        [InlineData("/tcp/1234/ip4/1.2.3.4", false)]
        [InlineData("/tcp/1234", false)]
        [InlineData("/tcp/1234/udp/1234", false)]
        [InlineData("/ip4/1.2.3.4/ip4/2.3.4.5", true)]
        [InlineData("/ip6/::1/ip4/2.3.4.5", true)]
        public void TestThinWaist(string addr, bool expected)
        {
            var m = Multiaddress.Decode(addr);

            Assert.Equal(expected, m.IsThinWaist());
        }

        [Fact]
        public void CanGetInterfaceAddresses()
        {
#if !__MonoCS__
            var addrs = MultiaddressTools.GetInterfaceMultiaddresses();

            Assert.True(addrs.Count() > 1);
#endif       
        }

        private void TestAddr(Multiaddress m, Multiaddress[] input, Multiaddress[] expect)
        {
            var actual = m.Match(input);

            Assert.Equal(expect, actual);
        }

        [Fact]
        public void TestAddrMatch()
        {
            var a = new[]
            {
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234"),
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/2345"),
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/tcp/2345"),
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/tcp/2345"),
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/udp/1234"),
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/udp/1234"),
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/ip6/::1"),
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/ip6/::1"),
                Multiaddress.Decode("/ip6/::1/tcp/1234"),
                Multiaddress.Decode("/ip6/::1/tcp/2345"),
                Multiaddress.Decode("/ip6/::1/tcp/1234/tcp/2345"),
                Multiaddress.Decode("/ip6/::1/tcp/1234/tcp/2345"),
                Multiaddress.Decode("/ip6/::1/tcp/1234/udp/1234"),
                Multiaddress.Decode("/ip6/::1/tcp/1234/udp/1234"),
                Multiaddress.Decode("/ip6/::1/tcp/1234/ip6/::1"),
                Multiaddress.Decode("/ip6/::1/tcp/1234/ip6/::1"),
            };

            TestAddr(a[0], a, new []
            {
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234"), 
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/2345"), 
            });

            TestAddr(a[2], a, new[]
            {
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/tcp/2345"),
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/tcp/2345"),
            });

            TestAddr(a[4], a, new[]
            {
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/udp/1234"),
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/udp/1234"),
            });

            TestAddr(a[6], a, new[]
            {
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/ip6/::1"),
                Multiaddress.Decode("/ip4/1.2.3.4/tcp/1234/ip6/::1"),
            });

            TestAddr(a[8], a, new[]
            {
                Multiaddress.Decode("/ip6/::1/tcp/1234"),
                Multiaddress.Decode("/ip6/::1/tcp/2345"),
            });

            TestAddr(a[10], a, new[]
            {
                Multiaddress.Decode("/ip6/::1/tcp/1234/tcp/2345"),
                Multiaddress.Decode("/ip6/::1/tcp/1234/tcp/2345"),
            });

            TestAddr(a[12], a, new[]
            {
                Multiaddress.Decode("/ip6/::1/tcp/1234/udp/1234"),
                Multiaddress.Decode("/ip6/::1/tcp/1234/udp/1234"),
            });

            TestAddr(a[14], a, new[]
            {
                Multiaddress.Decode("/ip6/::1/tcp/1234/ip6/::1"),
                Multiaddress.Decode("/ip6/::1/tcp/1234/ip6/::1"),
            });
        }
    }
}
