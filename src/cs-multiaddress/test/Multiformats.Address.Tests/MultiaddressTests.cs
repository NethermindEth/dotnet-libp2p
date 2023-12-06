using System;
using System.Linq;
using Multiformats.Address.Protocols;
using Org.BouncyCastle.Utilities.Encoders;
using Xunit;

namespace Multiformats.Address.Tests
{
    public class MultiaddressTests
    {
        [Theory]
        [InlineData("/ip4")]
        [InlineData("/ip4/::1")]
        [InlineData("/ip4/fdpsofodsajfdoisa")]
        [InlineData("/ip6")]
		[InlineData("/udp")]
		[InlineData("/tcp")]
		[InlineData("/sctp")]
		[InlineData("/udp/65536")]
		[InlineData("/tcp/65536")]
		[InlineData("/onion/9imaq4ygg2iegci7:80")]
		[InlineData("/onion/aaimaq4ygg2iegci7:80")]
		[InlineData("/onion/timaq4ygg2iegci7:0")]
		[InlineData("/onion/timaq4ygg2iegci7:-1")]
		[InlineData("/onion/timaq4ygg2iegci7")]
		[InlineData("/onion/timaq4ygg2iegci@:666")]
		[InlineData("/udp/1234/sctp")]
		[InlineData("/udp/1234/udt/1234")]
		[InlineData("/udp/1234/utp/1234")]
		[InlineData("/ip4/127.0.0.1/udp/jfodsajfidosajfoidsa")]
		[InlineData("/ip4/127.0.0.1/udp")]
		[InlineData("/ip4/127.0.0.1/tcp/jfodsajfidosajfoidsa")]
		[InlineData("/ip4/127.0.0.1/tcp")]
		[InlineData("/ip4/127.0.0.1/ipfs")]
		[InlineData("/ip4/127.0.0.1/ipfs/tcp")]
		[InlineData("/unix")]
		[InlineData("/ip4/1.2.3.4/tcp/80/unix")]
        [InlineData("/dns")]
        [InlineData("/dns4")]
        [InlineData("/dns6")]
        [InlineData("/libp2p-circuit-relay")]
        [InlineData("/libp2p-webrtc-star/4")]
        [InlineData("/libp2p-webrtc-direct/4")]
        public void TestConstructFails(string addr)
        {
            Assert.ThrowsAny<Exception>(() => Multiaddress.Decode(addr));
        }

        [Theory]
        [InlineData("/ip4/1.2.3.4")]
        [InlineData("/ip4/0.0.0.0")]
        [InlineData("/ip6/::1")]
        [InlineData("/ip6/2601:9:4f81:9700:803e:ca65:66e8:c21")]
        [InlineData("/onion/timaq4ygg2iegci7:1234")]
        [InlineData("/onion/timaq4ygg2iegci7:80/http")]
        [InlineData("/udp/0")]
        [InlineData("/tcp/0")]
        [InlineData("/sctp/0")]
        [InlineData("/udp/1234")]
        [InlineData("/tcp/1234")]
        [InlineData("/sctp/1234")]
        [InlineData("/udp/65535")]
        [InlineData("/tcp/65535")]
        [InlineData("/ipfs/QmcgpsyWgH8Y8ajJz1Cu72KnS5uo2Aa2LpzU7kinSupNKC")]
        [InlineData("/p2p/QmcgpsyWgH8Y8ajJz1Cu72KnS5uo2Aa2LpzU7kinSupNKC")]
        [InlineData("/udp/1234/sctp/1234")]
        [InlineData("/udp/1234/udt")]
        [InlineData("/udp/1234/utp")]
        [InlineData("/tcp/1234/http")]
        [InlineData("/tcp/1234/https")]
        [InlineData("/ipfs/QmcgpsyWgH8Y8ajJz1Cu72KnS5uo2Aa2LpzU7kinSupNKC/tcp/1234")]
        [InlineData("/p2p/QmcgpsyWgH8Y8ajJz1Cu72KnS5uo2Aa2LpzU7kinSupNKC/tcp/1234")]
        [InlineData("/ip4/127.0.0.1/udp/1234")]
        [InlineData("/ip4/127.0.0.1/udp/0")]
        [InlineData("/ip4/127.0.0.1/tcp/1234")]
        [InlineData("/ip4/127.0.0.1/tcp/1234/")]
        [InlineData("/ip4/127.0.0.1/ipfs/QmcgpsyWgH8Y8ajJz1Cu72KnS5uo2Aa2LpzU7kinSupNKC")]
        [InlineData("/ip4/127.0.0.1/ipfs/QmcgpsyWgH8Y8ajJz1Cu72KnS5uo2Aa2LpzU7kinSupNKC/tcp/1234")]
        [InlineData("/unix/a/b/c/d/e")]
        [InlineData("/unix/stdio")]
        [InlineData("/ip4/1.2.3.4/tcp/80/unix/a/b/c/d/e/f")]
        [InlineData("/ip4/127.0.0.1/ipfs/QmcgpsyWgH8Y8ajJz1Cu72KnS5uo2Aa2LpzU7kinSupNKC/tcp/1234/unix/stdio")]
        [InlineData("/dns/www.google.com")]
        [InlineData("/dns4/www.google.com")]
        [InlineData("/dns6/www.google.com")]
        [InlineData("/p2p-circuit/ipfs/QmcgpsyWgH8Y8ajJz1Cu72KnS5uo2Aa2LpzU7kinSupNKC")]
        [InlineData("/p2p-webrtc-star/dns/www.google.com")]
        [InlineData("/p2p-webrtc-direct/dns/www.google.com")]
        [InlineData("/p2p-websocket-star/dns/www.google.com")]
        [InlineData("/quic/dns/www.google.com")]
        [InlineData("/ws/dns/www.google.com")]
        [InlineData("/wss/dns/www.google.com")]
        [InlineData("/http/dns/www.google.com")]
        [InlineData("/https/dns/www.google.com")]
        public void TestConstructSucceeds(string addr)
        {
            Multiaddress.Decode(addr);
        }

        [Fact]
        public void TestEqual()
        {
            var m1 = Multiaddress.Decode("/ip4/127.0.0.1/udp/1234");
            var m2 = Multiaddress.Decode("/ip4/127.0.0.1/tcp/1234");
            var m3 = Multiaddress.Decode("/ip4/127.0.0.1/tcp/1234");
            var m4 = Multiaddress.Decode("/ip4/127.0.0.1/tcp/1234");

            Assert.NotEqual(m1, m2);
            Assert.NotEqual(m2, m1);
            Assert.Equal(m2, m3);
            Assert.Equal(m3, m2);
            Assert.Equal(m1, m1);
            Assert.Equal(m2, m4);
            Assert.Equal(m4, m3);
        }

        [Theory]
        [InlineData("/ip4/127.0.0.1/udp/1234", "047f0000011104d2")]
        [InlineData("/ip4/127.0.0.1/tcp/4321", "047f0000010610e1")]
        [InlineData("/ip4/127.0.0.1/udp/1234/ip4/127.0.0.1/tcp/4321", "047f0000011104d2047f0000010610e1")]
        public void TestStringToBytes(string s, string h)
        {
            var b1 = Hex.Decode(h);
            var b2 = Multiaddress.Decode(s).ToBytes();

            Assert.Equal(b1, b2);
        }

        [Theory]
        [InlineData("/ip4/127.0.0.1/udp/1234", "047f0000011104d2")]
        [InlineData("/ip4/127.0.0.1/tcp/4321", "047f0000010610e1")]
        [InlineData("/ip4/127.0.0.1/udp/1234/ip4/127.0.0.1/tcp/4321", "047f0000011104d2047f0000010610e1")]
        [InlineData("/onion/aaimaq4ygg2iegci:80", "bc030010c0439831b48218480050")]
        public void TestBytesToString(string s1, string h)
        {
            var b = Hex.Decode(h);
            var s2 = Multiaddress.Decode(s1).ToString();

            Assert.Equal(s1, s2);
        }

        [Theory]
        [InlineData("/ip4/1.2.3.4/udp/1234", "/ip4/1.2.3.4", "/udp/1234")]
        [InlineData("/ip4/1.2.3.4/tcp/1/ip4/2.3.4.5/udp/2", "/ip4/1.2.3.4", "/tcp/1", "/ip4/2.3.4.5", "/udp/2")]
        [InlineData("/ip4/1.2.3.4/utp/ip4/2.3.4.5/udp/2/udt", "/ip4/1.2.3.4", "/utp", "/ip4/2.3.4.5", "/udp/2", "/udt")]
        public void TestBytesSplitAndJoin(string s, params string[] res)
        {
            var m = Multiaddress.Decode(s);
            var split = m.Split().ToArray();

            Assert.Equal(split.Length, res.Length);

            for (var i = 0; i < split.Length; i++)
            {
                Assert.Equal(split[i].ToString(), res[i]);
            }

            var joined = Multiaddress.Join(split);
            Assert.Equal(m, joined);
        }

        [Fact]
        public void TestEncapsulate()
        {
            var m = Multiaddress.Decode("/ip4/127.0.0.1/udp/1234");
            var m2 = Multiaddress.Decode("/udp/5678");

            var b = m.Encapsulate(m2);
            Assert.Equal("/ip4/127.0.0.1/udp/1234/udp/5678", b.ToString());

            var m3 = Multiaddress.Decode("/udp/5678");
            var c = b.Decapsulate(m3);
            Assert.Equal("/ip4/127.0.0.1/udp/1234", c.ToString());

            var m4 = Multiaddress.Decode("/ip4/127.0.0.1");
            var d = c.Decapsulate(m4);
            Assert.Equal("", d.ToString());
        }

        [Fact]
        public void TestGetValue()
        {
            var a =
                Multiaddress.Decode(
                    "/ip4/127.0.0.1/utp/tcp/5555/udp/1234/utp/ipfs/QmbHVEEepCi7rn7VL7Exxpd2Ci9NNB6ifvqwhsrbRMgQFP");

            Assert.Equal("127.0.0.1", a.Protocols.OfType<IP4>().FirstOrDefault()?.ToString());
            Assert.Equal("", a.Protocols.OfType<UTP>().FirstOrDefault()?.ToString());
            Assert.Equal("5555", a.Protocols.OfType<TCP>().FirstOrDefault()?.ToString());
            Assert.Equal("1234", a.Protocols.OfType<UDP>().FirstOrDefault()?.ToString());
            Assert.Equal("QmbHVEEepCi7rn7VL7Exxpd2Ci9NNB6ifvqwhsrbRMgQFP", a.Protocols.OfType<IPFS>().FirstOrDefault()?.ToString());

            a = Multiaddress.Decode("/ip4/0.0.0.0/unix/a/b/c/d");
            Assert.Equal("0.0.0.0", a.Protocols.OfType<IP4>().FirstOrDefault()?.ToString());
            Assert.Equal("a/b/c/d", a.Protocols.OfType<Unix>().FirstOrDefault()?.ToString());
        }
    }
}
