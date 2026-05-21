// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using NSubstitute;
using NUnit.Framework;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core.Tests;

public class MultiaddrResolverTests
{
    [Test]
    public async Task Test_Resolve_Dnsaddr_UsesInjectedResolver()
    {
        // Arrange: mock DNS TXT records for _dnsaddr.bootstrap.libp2p.io
        var dns = NSubstitute.Substitute.For<IDnsLookup>();
        string dnsName = "_dnsaddr.bootstrap.libp2p.io";
        var txtRecords = new[] {
            "dnsaddr=/ip4/104.131.131.82/tcp/4001/p2p/QmaCpDMGvV2BGHeYERUEnRQAwe3N8SzbUtfsmvsqQLuvuJ",
            "dnsaddr=/ip4/1.2.3.4/tcp/4001/p2p/QmNLei78zWmzUdbeRB3CiUfAizWUrbeeZh5K1rhAQKCh51"
        };
        dns.QueryTxtAsync(dnsName).Returns(Task.FromResult((IEnumerable<string>)txtRecords));

        var resolver = new MultiaddrResolver(dns);

        // Act
        var results = new List<Multiaddress>();
        Multiaddress input = "/dnsaddr/bootstrap.libp2p.io/p2p/QmaCpDMGvV2BGHeYERUEnRQAwe3N8SzbUtfsmvsqQLuvuJ";
        await foreach (var item in resolver.Resolve(input))
        {
            results.Add(item);
        }

        // Assert: expect to get the first address (matching the p2p filter)
        Assert.That(results.Count, Is.GreaterThan(0));
        Assert.That(results[0].ToString(), Is.EqualTo("/ip4/104.131.131.82/tcp/4001/p2p/QmaCpDMGvV2BGHeYERUEnRQAwe3N8SzbUtfsmvsqQLuvuJ"));
    }
}
