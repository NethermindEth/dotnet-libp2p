// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.TestsBase;
using System.Net;

namespace Nethermind.Libp2p.Core.Tests;

public class DnsLookupTests
{
    [TestCase("dns4")]
    [TestCase("dns6")]
    [TestCase("dnsaddr")]
    public async Task ResolverAcceptsImplementationsOfTheOriginalInterface(string protocol)
    {
        LegacyLookup legacy = new();
        legacy.Release.SetResult();
        List<Multiaddress> results = [];

        await foreach (Multiaddress address in new MultiaddrResolver(legacy).Resolve($"/{protocol}/legacy.test/tcp/4001/p2p/{TestPeers.PeerId(2)}")) results.Add(address);

        string expected = protocol == "dns6"
            ? $"/ip6/2001:db8::1/tcp/4001/p2p/{TestPeers.PeerId(2)}"
            : $"/ip4/192.0.2.1/tcp/4001/p2p/{TestPeers.PeerId(2)}";
        Assert.That(results.Select(a => a.ToString()), Is.EqualTo(new[] { expected }));
        Assert.That(legacy.Calls, Is.EqualTo(1));
    }

    [TestCase("dns4")]
    [TestCase("dns6")]
    [TestCase("dnsaddr")]
    public async Task DefaultOverloadsCancelWaitingForLegacyQueries(string protocol)
    {
        LegacyLookup legacy = new();
        using CancellationTokenSource cancellation = new();
        Task query = Query(legacy, protocol, cancellation.Token);
        try
        {
            Assert.That(legacy.Calls, Is.EqualTo(1));
            cancellation.Cancel();
            Assert.That(query.IsCanceled, Is.True, "The default overload must stop waiting even when the legacy operation cannot be canceled.");
            Assert.CatchAsync<OperationCanceledException>(async () => await query);
            Assert.That(legacy.Release.Task.IsCompleted, Is.False);
        }
        finally
        {
            legacy.Release.TrySetResult();
        }
    }

    [TestCase("dns4")]
    [TestCase("dns6")]
    [TestCase("dnsaddr")]
    public void DefaultOverloadsDoNotStartCanceledLegacyQueries(string protocol)
    {
        LegacyLookup legacy = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await Query(legacy, protocol, cancellation.Token));

        Assert.That(legacy.Calls, Is.Zero);
    }

    [TestCase(nameof(IDnsLookup.QueryTxtAsync))]
    [TestCase(nameof(IDnsLookup.QueryAAsync))]
    [TestCase(nameof(IDnsLookup.QueryAaaaAsync))]
    public void ExistingDnsMembersRemainAvailable(string method)
    {
        Assert.That(typeof(IDnsLookup).GetMethod(method, [typeof(string)]), Is.Not.Null,
            "Existing DNS implementations must retain their original interface contract.");
        Assert.That(typeof(DnsClientLookup).GetMethod(method, [typeof(string)]), Is.Not.Null);
        Assert.That(typeof(IDnsLookup).GetMethod(method, [typeof(string), typeof(CancellationToken)])!.IsAbstract, Is.False,
            "Cancellation support must have a default for existing implementations.");
    }

    private static Task Query(IDnsLookup dns, string protocol, CancellationToken token) => protocol switch
    {
        "dns4" => dns.QueryAAsync("legacy.test", token),
        "dns6" => dns.QueryAaaaAsync("legacy.test", token),
        _ => dns.QueryTxtAsync("_dnsaddr.legacy.test", token)
    };

    private sealed class LegacyLookup : IDnsLookup
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<IEnumerable<string>> QueryTxtAsync(string name) => CompleteAsync<string>([$"dnsaddr=/ip4/192.0.2.1/tcp/4001/p2p/{TestPeers.PeerId(2)}"]);
        public Task<IEnumerable<IPAddress>> QueryAAsync(string name) => CompleteAsync<IPAddress>([IPAddress.Parse("192.0.2.1")]);
        public Task<IEnumerable<IPAddress>> QueryAaaaAsync(string name) => CompleteAsync<IPAddress>([IPAddress.Parse("2001:db8::1")]);

        private async Task<IEnumerable<T>> CompleteAsync<T>(IEnumerable<T> result)
        {
            Calls++;
            await Release.Task;
            return result;
        }
    }
}
