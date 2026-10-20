// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Runtime.CompilerServices;
using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Core;

public class MultiaddrResolver(IDnsLookup? dns = null, TimeProvider? timeProvider = null)
{
    // DNS budgets apply across all roots and nested records; caller-supplied literals are retained.
    private const int MaxQueries = 32;
    private const int MaxDepth = 8;
    private const int MaxDnsAddresses = 16;
    private const int MaxRecordsPerQuery = 32;
    private static readonly TimeSpan ResolutionTimeout = TimeSpan.FromSeconds(15);
    private readonly IDnsLookup _dns = dns ?? new DnsClientLookup();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Resolves DNS addresses with bounded queries, recursion depth and distinct DNS results.
    /// DNS queries share a 15-second deadline. Failed names and malformed DNSADDR records
    /// are skipped; literal addresses remain usable after the DNS deadline expires.
    /// </summary>
    public IAsyncEnumerable<Multiaddress> Resolve(Multiaddress addr, CancellationToken token = default) => Resolve([addr], token);

    internal async IAsyncEnumerable<Multiaddress> Resolve(IEnumerable<Multiaddress> addresses, [EnumeratorCancellation] CancellationToken token = default)
    {
        using IEnumerator<Multiaddress> roots = addresses.GetEnumerator();
        Queue<(Multiaddress Address, int Depth)> pending = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        HashSet<string> results = new(StringComparer.Ordinal);
        int queries = 0;
        int dnsAddresses = 0;
        using CancellationTokenSource timeout = new(ResolutionTimeout, _timeProvider);
        using CancellationTokenSource queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        async Task<T[]> QueryAsync<T>(Func<CancellationToken, Task<IEnumerable<T>>> query)
        {
            token.ThrowIfCancellationRequested();
            if (queries >= MaxQueries || timeout.IsCancellationRequested) return [];
            queries++;
            try
            {
                IEnumerable<T> records = await query(queryCancellation.Token);
                token.ThrowIfCancellationRequested();
                return records.Take(MaxRecordsPerQuery).ToArray();
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                return [];
            }
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!pending.TryDequeue(out var next))
            {
                if (!roots.MoveNext()) yield break;
                next = (roots.Current, 0);
            }
            Multiaddress addr = next.Address;
            // Secure WebSocket transports need the hostname for TLS verification.
            bool preserveHostname = addr.Has<WebSocketSecure>();
            bool needsDns = addr.Has<DnsAddr>() || !preserveHostname && (addr.Has<DNS>() || addr.Has<DNS4>() || addr.Has<DNS6>());
            if (next.Depth == 0 && !needsDns)
            {
                if (results.Add(addr.ToString())) yield return addr;
                continue;
            }
            if (dnsAddresses >= MaxDnsAddresses || needsDns && (queries >= MaxQueries || timeout.IsCancellationRequested)) continue;
            if (!visited.Add(addr.ToString())) continue;

            if (addr.Has<DnsAddr>())
            {
                if (next.Depth >= MaxDepth) continue;
                PeerId? peerId = addr.GetPeerId();
                string[] records = await QueryAsync(t => _dns.QueryTxtAsync($"_dnsaddr.{addr.Get<DnsAddr>()}", t));
                foreach (string text in records)
                {
                    const string prefix = "dnsaddr=";
                    if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    Multiaddress resolved;
                    try
                    {
                        resolved = text[prefix.Length..];
                    }
                    catch (Exception e) when (e is ArgumentException or FormatException or NotSupportedException)
                    {
                        continue;
                    }
                    if ((peerId is null || resolved.GetPeerId() == peerId) && pending.Count < MaxRecordsPerQuery)
                    {
                        pending.Enqueue((resolved, next.Depth + 1));
                    }
                }
                continue;
            }

            if (!preserveHostname && (addr.Has<DNS6>() || addr.Has<DNS>()))
            {
                IPAddress[] records = await QueryAsync(t => _dns.QueryAaaaAsync(addr.Get<DNS6>()?.ToString() ?? addr.Get<DNS>().ToString(), t));
                foreach (IPAddress record in records)
                {
                    if (dnsAddresses >= MaxDnsAddresses) break;
                    Multiaddress resolved = addr.Has<DNS6>() ? addr.Clone().Replace<DNS6, IP6>(record) : addr.Clone().Replace<DNS, IP6>(record);
                    if (results.Add(resolved.ToString()))
                    {
                        dnsAddresses++;
                        yield return resolved;
                    }
                }
            }
            if (dnsAddresses < MaxDnsAddresses && !preserveHostname && (addr.Has<DNS4>() || addr.Has<DNS>()))
            {
                IPAddress[] records = await QueryAsync(t => _dns.QueryAAsync(addr.Get<DNS4>()?.ToString() ?? addr.Get<DNS>().ToString(), t));
                foreach (IPAddress record in records)
                {
                    if (dnsAddresses >= MaxDnsAddresses) break;
                    Multiaddress resolved = addr.Has<DNS4>() ? addr.Clone().Replace<DNS4, IP4>(record) : addr.Clone().Replace<DNS, IP4>(record);
                    if (results.Add(resolved.ToString()))
                    {
                        dnsAddresses++;
                        yield return resolved;
                    }
                }
            }
            if ((preserveHostname || !(addr.Has<DNS>() || addr.Has<DNS4>() || addr.Has<DNS6>())) && results.Add(addr.ToString()))
            {
                dnsAddresses++;
                yield return addr;
            }
        }
    }
}
