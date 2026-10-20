// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using System.Net;

namespace Nethermind.Libp2p.Core.Tests;

public class MultiaddrResolutionLimitsTests
{
    private static readonly PeerId Remote = TestPeers.PeerId(2);

    [TestCase(16)]
    [TestCase(17)]
    [TestCase(32)]
    [TestCase(33)]
    public async Task LiteralRootsAreNotLimitedByDnsBudgets(int count)
    {
        Multiaddress[] roots = Enumerable.Range(1, count)
            .Select(i => (Multiaddress)$"/ip4/192.0.2.{i}/tcp/4001/p2p/{Remote}").ToArray();
        List<Multiaddress> results = [];

        await foreach (Multiaddress address in new MultiaddrResolver().Resolve(roots)) results.Add(address);

        Assert.That(results.Select(a => a.ToString()), Is.EqualTo(roots.Select(a => a.ToString())));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DnsResultLimitDoesNotDiscardLiteralRoots(bool literalFirst)
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        dns.QueryAAsync("many.test", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IEnumerable<IPAddress>>(
            Enumerable.Range(1, 17).Select(i => IPAddress.Parse($"192.0.2.{i}"))));
        Multiaddress literal = $"/ip4/198.51.100.1/tcp/4001/p2p/{Remote}";
        Multiaddress name = $"/dns4/many.test/tcp/4001/p2p/{Remote}";
        List<Multiaddress> results = [];

        await foreach (Multiaddress address in new MultiaddrResolver(dns).Resolve(literalFirst ? [literal, name] : [name, literal])) results.Add(address);

        string[] derived = Enumerable.Range(1, 16).Select(i => $"/ip4/192.0.2.{i}/tcp/4001/p2p/{Remote}").ToArray();
        Assert.That(results.Select(a => a.ToString()), Is.EqualTo(literalFirst ? new[] { literal.ToString() }.Concat(derived) : derived.Concat([literal.ToString()])));
    }

    [Test]
    public async Task ExhaustedQueryBudgetDoesNotDiscardLaterLiteralRoot()
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        dns.QueryAAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IEnumerable<IPAddress>>([]));
        Multiaddress literal = $"/ip4/198.51.100.1/tcp/4001/p2p/{Remote}";
        Multiaddress[] roots = [.. Enumerable.Range(0, 33).Select(i => (Multiaddress)$"/dns4/name{i}.test/tcp/4001/p2p/{Remote}"), literal];
        List<Multiaddress> results = [];

        await foreach (Multiaddress address in new MultiaddrResolver(dns).Resolve(roots)) results.Add(address);

        Assert.That(results.Select(a => a.ToString()), Is.EqualTo(new[] { literal.ToString() }));
        await dns.Received(32).QueryAAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CyclicRecordsAreNotQueriedAgain()
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        int calls = 0;
        dns.QueryTxtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            calls++;
            string next = call.Arg<string>() == "_dnsaddr.first.test" ? "second.test" : "first.test";
            // Stop the old implementation safely after proving that it repeats the cycle.
            return Task.FromResult<IEnumerable<string>>(calls < 4 ? [$"dnsaddr=/dnsaddr/{next}/p2p/{Remote}"] : []);
        });

        Assert.That(await CollectAsync(new MultiaddrResolver(dns), $"/dnsaddr/first.test/p2p/{Remote}"), Is.Empty);
        Assert.That(calls, Is.EqualTo(2));
    }

    [Test]
    public async Task RecursiveRecordsStopAtTheDepthLimit()
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        int calls = 0;
        dns.QueryTxtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            int depth = ++calls;
            return Task.FromResult<IEnumerable<string>>(depth < 40
                ? [$"dnsaddr=/dnsaddr/level{depth}.test/p2p/{Remote}"]
                : [$"dnsaddr=/ip4/127.0.0.1/tcp/4001/p2p/{Remote}"]);
        });

        Assert.That(await CollectAsync(new MultiaddrResolver(dns), $"/dnsaddr/start.test/p2p/{Remote}"), Is.Empty);
        Assert.That(calls, Is.EqualTo(8));
    }

    [TestCase(15)]
    [TestCase(16)]
    [TestCase(17)]
    public async Task AddressCountIsBounded(int count)
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        dns.QueryAAsync("many.test", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IEnumerable<IPAddress>>(
            Enumerable.Range(1, count).Select(i => IPAddress.Parse($"192.0.2.{i}"))));

        List<Multiaddress> addresses = await CollectAsync(new MultiaddrResolver(dns), $"/dns4/many.test/tcp/4001/p2p/{Remote}");

        Assert.That(addresses.Select(a => a.ToString()), Is.EqualTo(Enumerable.Range(1, Math.Min(count, 16)).Select(i => $"/ip4/192.0.2.{i}/tcp/4001/p2p/{Remote}")));
    }

    [Test]
    public async Task FailedNestedNameDoesNotDiscardOtherRecords()
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        string valid = $"/ip4/192.0.2.1/tcp/4001/p2p/{Remote}";
        dns.QueryTxtAsync("_dnsaddr.root.test", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IEnumerable<string>>(
            [$"dnsaddr=/dns4/broken.test/tcp/4001/p2p/{Remote}", $"dnsaddr={valid}"]));
        dns.QueryAAsync("broken.test", Arg.Any<CancellationToken>()).Returns(Task.FromException<IEnumerable<IPAddress>>(new DnsClient.DnsResponseException("lookup failed")));

        Assert.That((await CollectAsync(new MultiaddrResolver(dns), $"/dnsaddr/root.test/p2p/{Remote}")).Select(a => a.ToString()), Is.EqualTo(new[] { valid }));
    }

    [Test]
    public async Task MalformedRecordDoesNotDiscardValidSibling()
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        string valid = $"/ip4/192.0.2.1/tcp/4001/p2p/{Remote}";
        dns.QueryTxtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IEnumerable<string>>(["dnsaddr=malformed", $"dnsaddr={valid}"]));

        Assert.That((await CollectAsync(new MultiaddrResolver(dns), $"/dnsaddr/root.test/p2p/{Remote}")).Select(a => a.ToString()), Is.EqualTo(new[] { valid }));
    }

    [TestCase("dns4")]
    [TestCase("dns6")]
    [TestCase("dnsaddr")]
    public async Task CancellationReachesTheInFlightDnsQuery(string protocol)
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        CancellationToken queryToken = default;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IEnumerable<T>> WaitForCancellation<T>(CancellationToken token)
        {
            queryToken = token;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return [];
        }
        dns.QueryAAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(c => WaitForCancellation<IPAddress>(c.Arg<CancellationToken>()));
        dns.QueryAaaaAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(c => WaitForCancellation<IPAddress>(c.Arg<CancellationToken>()));
        dns.QueryTxtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(c => WaitForCancellation<string>(c.Arg<CancellationToken>()));
        using CancellationTokenSource cancellation = new();
        Task<List<Multiaddress>> resolve = CollectAsync(new MultiaddrResolver(dns), $"/{protocol}/pending.test/tcp/4001/p2p/{Remote}", cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(queryToken.CanBeCanceled, Is.True);
            cancellation.Cancel();
            Assert.That(queryToken.IsCancellationRequested, Is.True);
            Assert.CatchAsync<OperationCanceledException>(async () => await resolve.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await ((Task)resolve).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task QueryBudgetIsSharedAcrossAllRootAddresses()
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        int queries = 0;
        dns.QueryAAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            queries++;
            return Task.FromResult<IEnumerable<IPAddress>>([]);
        });
        dns.QueryAaaaAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            queries++;
            return Task.FromResult<IEnumerable<IPAddress>>([]);
        });
        Multiaddress[] roots = Enumerable.Range(0, 20).Select(i => (Multiaddress)$"/dns/name{i}.test/tcp/4001/p2p/{Remote}").ToArray();
        await foreach (Multiaddress _ in new MultiaddrResolver(dns).Resolve(roots)) { }

        Assert.That(queries, Is.EqualTo(32));
    }

    [Test]
    public async Task DuplicateResultsDoNotConsumeAddressCapacity()
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        dns.QueryAAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IEnumerable<IPAddress>>(
            Enumerable.Range(1, 17).SelectMany(i => Enumerable.Repeat(IPAddress.Parse($"192.0.2.{i}"), 2))));

        Assert.That((await CollectAsync(new MultiaddrResolver(dns), $"/dns4/many.test/tcp/4001/p2p/{Remote}")).Select(a => a.ToString()),
            Is.EqualTo(Enumerable.Range(1, 16).Select(i => $"/ip4/192.0.2.{i}/tcp/4001/p2p/{Remote}")));
    }

    [TestCase(7, true)]
    [TestCase(8, true)]
    [TestCase(9, false)]
    public async Task DepthBoundaryPreservesReachableAddresses(int levels, bool reachable)
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        int queries = 0;
        string valid = $"/ip4/192.0.2.1/tcp/4001/p2p/{Remote}";
        dns.QueryTxtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromResult<IEnumerable<string>>([++queries == levels ? $"dnsaddr={valid}" : $"dnsaddr=/dnsaddr/level{queries}.test/p2p/{Remote}"]));

        List<Multiaddress> addresses = await CollectAsync(new MultiaddrResolver(dns), $"/dnsaddr/root.test/p2p/{Remote}");

        Assert.That(addresses.Select(a => a.ToString()), Is.EqualTo(reachable ? new[] { valid } : Array.Empty<string>()));
        Assert.That(queries, Is.EqualTo(Math.Min(levels, 8)));
    }

    [Test]
    public async Task FailedIpv6QueryStillAllowsIpv4Results()
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        dns.QueryAaaaAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<IEnumerable<IPAddress>>(new DnsClient.DnsResponseException("lookup failed")));
        dns.QueryAAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IEnumerable<IPAddress>>([IPAddress.Parse("192.0.2.1")]));

        Assert.That((await CollectAsync(new MultiaddrResolver(dns), $"/dns/dual.test/tcp/4001/p2p/{Remote}")).Select(a => a.ToString()),
            Is.EqualTo(new[] { $"/ip4/192.0.2.1/tcp/4001/p2p/{Remote}" }));
    }

    private static async Task<List<Multiaddress>> CollectAsync(MultiaddrResolver resolver, Multiaddress address, CancellationToken token = default)
    {
        List<Multiaddress> results = [];
        await foreach (Multiaddress resolved in resolver.Resolve(address, token)) results.Add(resolved);
        return results;
    }
}
