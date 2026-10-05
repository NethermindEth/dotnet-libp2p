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
    public async Task Test_Resolve_RelayedAddress_UsesTargetPeerId()
    {
        Multiaddress input = "/ip4/127.0.0.1/tcp/4001/p2p/12D3KooWGCs2ta5wWxwQ66xC5C34gXWPtd84rgja7guQ7wjqZJJF/p2p-circuit/p2p/12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA";
        MultiaddrResolver resolver = new();

        List<Multiaddress> results = [];
        await foreach (Multiaddress item in resolver.Resolve(input))
        {
            results.Add(item);
        }

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0], Is.EqualTo(input));
        Assert.That(results[0].GetPeerId(), Is.EqualTo(new PeerId("12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA")));
    }

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

    [Test]
    public async Task Test_Resolve_Dnsaddr_RelayedWebRtcAddress_FiltersRelayAndPreservesCircuitSuffix()
    {
        var dns = NSubstitute.Substitute.For<IDnsLookup>();
        string relayPeerId = "12D3KooWGCs2ta5wWxwQ66xC5C34gXWPtd84rgja7guQ7wjqZJJF";
        string targetPeerId = "12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA";
        string otherPeerId = "12D3KooWJRSrypvnpHgc6ZAgyCni4KcSmbV7uGRaMw5LgMKT18fq";
        string dnsName = "_dnsaddr.relay.example";
        var txtRecords = new[]
        {
            $"dnsaddr=/ip4/203.0.113.10/tcp/443/wss/p2p/{relayPeerId}",
            $"dnsaddr=/ip4/203.0.113.11/tcp/443/wss/p2p/{otherPeerId}"
        };
        dns.QueryTxtAsync(dnsName).Returns(Task.FromResult((IEnumerable<string>)txtRecords));

        MultiaddrResolver resolver = new(dns);
        Multiaddress input = $"/dnsaddr/relay.example/p2p/{relayPeerId}/p2p-circuit/webrtc/p2p/{targetPeerId}";
        List<Multiaddress> results = [];
        await foreach (Multiaddress item in resolver.Resolve(input))
        {
            results.Add(item);
        }

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].ToString(), Is.EqualTo($"/ip4/203.0.113.10/tcp/443/wss/p2p/{relayPeerId}/p2p-circuit/webrtc/p2p/{targetPeerId}"));
        Assert.That(results[0].GetPeerId(), Is.EqualTo(new PeerId(targetPeerId)));
    }
}
