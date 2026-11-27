// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

// cspell:ignore fbff fdff febf feff baaaaaaaaaaaaaaa

using System.Net;
using System.Net.Sockets;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

public class MultiaddressClassificationTests
{
    public enum Scope
    {
        Other,
        Loopback,
        Unspecified,
        LinkLocal,
        Private,
        PublicCandidate
    }

    [TestCase("0.0.0.0", Scope.Unspecified)]
    [TestCase("0.0.0.1", Scope.Other)]
    [TestCase("0.255.255.255", Scope.Other)]
    [TestCase("1.0.0.0", Scope.PublicCandidate)]
    [TestCase("9.255.255.255", Scope.PublicCandidate)]
    [TestCase("10.0.0.0", Scope.Private)]
    [TestCase("10.255.255.255", Scope.Private)]
    [TestCase("11.0.0.0", Scope.PublicCandidate)]
    [TestCase("100.63.255.255", Scope.PublicCandidate)]
    [TestCase("100.64.0.0", Scope.Other)]
    [TestCase("100.127.255.255", Scope.Other)]
    [TestCase("100.128.0.0", Scope.PublicCandidate)]
    [TestCase("126.255.255.255", Scope.PublicCandidate)]
    [TestCase("127.0.0.0", Scope.Loopback)]
    [TestCase("127.255.255.255", Scope.Loopback)]
    [TestCase("128.0.0.0", Scope.PublicCandidate)]
    [TestCase("169.253.255.255", Scope.PublicCandidate)]
    [TestCase("169.254.0.0", Scope.LinkLocal)]
    [TestCase("169.254.255.255", Scope.LinkLocal)]
    [TestCase("169.255.0.0", Scope.PublicCandidate)]
    [TestCase("172.15.255.255", Scope.PublicCandidate)]
    [TestCase("172.16.0.0", Scope.Private)]
    [TestCase("172.31.255.255", Scope.Private)]
    [TestCase("172.32.0.0", Scope.PublicCandidate)]
    [TestCase("192.0.0.9", Scope.Other)]
    [TestCase("192.0.0.10", Scope.Other)]
    [TestCase("192.0.0.255", Scope.Other)]
    [TestCase("192.0.1.0", Scope.PublicCandidate)]
    [TestCase("192.0.2.255", Scope.Other)]
    [TestCase("192.0.3.0", Scope.PublicCandidate)]
    [TestCase("192.31.196.1", Scope.Other)]
    [TestCase("192.52.193.1", Scope.Other)]
    [TestCase("192.88.99.2", Scope.Other)]
    [TestCase("192.167.255.255", Scope.PublicCandidate)]
    [TestCase("192.168.0.0", Scope.Private)]
    [TestCase("192.168.255.255", Scope.Private)]
    [TestCase("192.169.0.0", Scope.PublicCandidate)]
    [TestCase("192.175.48.1", Scope.Other)]
    [TestCase("198.17.255.255", Scope.PublicCandidate)]
    [TestCase("198.18.0.0", Scope.Other)]
    [TestCase("198.19.255.255", Scope.Other)]
    [TestCase("198.20.0.0", Scope.PublicCandidate)]
    [TestCase("198.51.100.1", Scope.Other)]
    [TestCase("203.0.113.1", Scope.Other)]
    [TestCase("223.255.255.255", Scope.PublicCandidate)]
    [TestCase("224.0.0.0", Scope.Other)]
    [TestCase("239.255.255.255", Scope.Other)]
    [TestCase("240.0.0.0", Scope.Other)]
    [TestCase("255.255.255.255", Scope.Other)]
    [TestCase("::", Scope.Unspecified)]
    [TestCase("::%7", Scope.Unspecified)]
    [TestCase("::1", Scope.Loopback)]
    [TestCase("::1%7", Scope.Loopback)]
    [TestCase("::2", Scope.Other)]
    [TestCase("::ffff:0.0.0.0", Scope.Unspecified)]
    [TestCase("::ffff:8.8.8.8", Scope.PublicCandidate)]
    [TestCase("::ffff:8.8.8.8%7", Scope.Other)]
    [TestCase("::ffff:10.0.0.1", Scope.Private)]
    [TestCase("::ffff:100.64.0.1", Scope.Other)]
    [TestCase("::ffff:127.0.0.2", Scope.Loopback)]
    [TestCase("::ffff:169.254.0.1", Scope.LinkLocal)]
    [TestCase("::ffff:203.0.113.1", Scope.Other)]
    [TestCase("64:ff9b::808:808", Scope.Other)]
    [TestCase("64:ff9b:1::1", Scope.Other)]
    [TestCase("100::1", Scope.Other)]
    [TestCase("100:0:0:1::1", Scope.Other)]
    [TestCase("1fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", Scope.Other)]
    [TestCase("2000::", Scope.PublicCandidate)]
    [TestCase("2001::", Scope.Other)]
    [TestCase("2001:1::1", Scope.Other)]
    [TestCase("2001:20::1", Scope.Other)]
    [TestCase("2001:1ff:ffff:ffff:ffff:ffff:ffff:ffff", Scope.Other)]
    [TestCase("2001:200::", Scope.PublicCandidate)]
    [TestCase("2001:db7:ffff:ffff:ffff:ffff:ffff:ffff", Scope.PublicCandidate)]
    [TestCase("2001:db8::", Scope.Other)]
    [TestCase("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", Scope.Other)]
    [TestCase("2001:db9::", Scope.PublicCandidate)]
    [TestCase("2002::1", Scope.Other)]
    [TestCase("2003::", Scope.PublicCandidate)]
    [TestCase("2606:4700::1111", Scope.PublicCandidate)]
    [TestCase("2606:4700::1111%7", Scope.Other)]
    [TestCase("2620:4f:8000::1", Scope.Other)]
    [TestCase("3fff::", Scope.Other)]
    [TestCase("3fff:fff:ffff:ffff:ffff:ffff:ffff:ffff", Scope.Other)]
    [TestCase("3fff:1000::", Scope.PublicCandidate)]
    [TestCase("4000::", Scope.Other)]
    [TestCase("5f00::1", Scope.Other)]
    [TestCase("fbff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", Scope.Other)]
    [TestCase("fc00::", Scope.Private)]
    [TestCase("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", Scope.Private)]
    [TestCase("fe00::", Scope.Other)]
    [TestCase("fe7f:ffff:ffff:ffff:ffff:ffff:ffff:ffff", Scope.Other)]
    [TestCase("fe80::", Scope.LinkLocal)]
    [TestCase("fe80::1%7", Scope.LinkLocal)]
    [TestCase("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff", Scope.LinkLocal)]
    [TestCase("fec0::", Scope.Private)]
    [TestCase("feff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", Scope.Private)]
    [TestCase("ff00::", Scope.Other)]
    [TestCase("ff02::1", Scope.Other)]
    public void ClassifiesLiteralIpAndMultiaddress(string literal, Scope scope)
    {
        IPAddress ip = IPAddress.Parse(literal);
        string family = ip.AddressFamily == AddressFamily.InterNetwork ? "ip4" : "ip6";
        Multiaddress address = $"/{family}/{literal}/tcp/4001";
        var expected = (scope == Scope.Loopback, scope == Scope.Unspecified, scope == Scope.LinkLocal,
            scope == Scope.Private, scope == Scope.PublicCandidate);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(address.GetDirectIPAddress(), Is.EqualTo(ip));
            Assert.That((ip.IsLoopbackAddress(), ip.IsUnspecified(), ip.IsLinkLocal(),
                ip.IsPrivateAddress(), ip.IsPublicAddressCandidate()), Is.EqualTo(expected), "IP predicates");
            Assert.That((address.IsLoopbackAddress(), address.IsUnspecified(), address.IsLinkLocal(),
                address.IsPrivateAddress(), address.IsPublicAddressCandidate()), Is.EqualTo(expected), "Multiaddress predicates");
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("/dns/localhost/tcp/4001")]
    [TestCase("/dns4/8.8.8.8/tcp/4001")]
    [TestCase("/dns6/example.com/tcp/4001")]
    [TestCase("/dnsaddr/bootstrap.libp2p.io/p2p/PEER")]
    [TestCase("/p2p/PEER")]
    [TestCase("/unix/tmp/libp2p.sock")]
    [TestCase("/onion/baaaaaaaaaaaaaaa:4001")]
    [TestCase("/ip4/8.8.8.8/tcp/4001/onion/baaaaaaaaaaaaaaa:4001")]
    [TestCase("/ip4/8.8.8.8/tcp/4001/unix/tmp/libp2p.sock")]
    [TestCase("/ip4/8.8.8.8/tcp/4001/p2p/PEER/p2p-circuit")]
    [TestCase("/ip4/8.8.8.8/tcp/4001/p2p/PEER/p2p-circuit/p2p/DESTINATION")]
    [TestCase("/p2p-circuit/ip6/::1/tcp/4001/p2p/DESTINATION")]
    [TestCase("/ip4/8.8.8.8/ip4/8.8.8.8/tcp/4001")]
    [TestCase("/ip4/8.8.8.8/ip6/2606:4700::1111/tcp/4001")]
    [TestCase("/ip4/8.8.8.8/tcp/4001/dns/example.com")]
    [TestCase("/dns4/example.com/ip4/8.8.8.8/tcp/4001")]
    [TestCase("/tcp/4001/ip4/8.8.8.8")]
    public void AddressesWithoutADirectLiteralIpAreUnclassified(string? text)
    {
        Multiaddress? address = text is null ? null : Multiaddress.Decode(text
            .Replace("PEER", TestPeers.PeerId(1).ToString(), StringComparison.Ordinal)
            .Replace("DESTINATION", TestPeers.PeerId(2).ToString(), StringComparison.Ordinal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(address.GetDirectIPAddress(), Is.Null);
            Assert.That((address.IsLoopbackAddress(), address.IsUnspecified(), address.IsLinkLocal(),
                address.IsPrivateAddress(), address.IsPublicAddressCandidate()), Is.EqualTo((false, false, false, false, false)));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void IpComponentWithoutAValueDoesNotProvideALiteralIp(bool ipv6)
    {
        Multiaddress address = new Multiaddress().Add(ipv6 ? new IP6() : new IP4());
        Assert.That(address.GetDirectIPAddress(), Is.Null);
    }

    [TestCase("/ip4/8.8.8.8/tcp/4001/ws")]
    [TestCase("/ip6/2606:4700::1111/udp/4001/quic-v1")]
    public void ClassificationPreservesTransportAndPeerSuffixes(string endpoint)
    {
        PeerId peer = TestPeers.PeerId(1);
        string original = $"{endpoint}/p2p/{peer}";
        Multiaddress address = original;
        byte[] bytes = address.ToBytes();

        Assert.That(address.IsPublicAddressCandidate(), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(address.GetPeerId(), Is.EqualTo(peer));
            Assert.That(address.ToString(), Is.EqualTo(original));
            Assert.That(address.ToBytes(), Is.EqualTo(bytes));
        }
    }
}
