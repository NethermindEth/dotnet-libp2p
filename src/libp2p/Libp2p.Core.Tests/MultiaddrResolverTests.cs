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
        var txts = new[] {
            "dnsaddr=/ip4/104.131.131.82/tcp/4001/p2p/QmaCpDMGvV2BGHeYERUEnRQAwe3N8SzbUtfsmvsqQLuvuJ",
            "dnsaddr=/ip4/1.2.3.4/tcp/4001/p2p/QmTestPeer"
        };
        dns.QueryTxtAsync(dnsName).Returns(Task.FromResult((IEnumerable<string>)txts));

        var resolver = new MultiaddrResolver(dns);

        // Act
        var results = new List<Multiaddress>();
        Multiaddress input = "/dnsaddr/bootstrap.libp2p.io/p2p/QmaCpDMGvV2BGHeYERUEnRQAwe3N8SzbUtfsmvsqQLuvuJ";
        await foreach (var item in resolver.Resolve(input))
        {
            results.Add(item);
        }

        // Assert: expect to get the addresses provided by the TXT records
        var expected = new HashSet<string>(txts.Select(t => t.Substring("dnsaddr=".Length)));
        var actual = new HashSet<string>(results.Select(r => r.ToString()));
        Assert.That(expected.IsSubsetOf(actual), Is.True);
    }
}
