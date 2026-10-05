// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using DnsClient;
using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Core;

public class MultiaddrResolver
{
    private readonly IDnsLookup _dns;

    public MultiaddrResolver(IDnsLookup? dns = null)
    {
        _dns = dns ?? new DnsClientLookup();
    }

    /// <summary>
    /// Converts DNS/DNS4/DNS6/dnsaddr to non-unique IP4/IP6-based addresses
    /// </summary>
    /// <param name="addr">A multiaddress</param>
    /// <returns>Resolved addresses</returns>
    public async IAsyncEnumerable<Multiaddress> Resolve(Multiaddress addr)
    {
        if (addr.Has<DnsAddr>())
        {
            DnsaddrResolutionContext resolutionContext = GetDnsaddrResolutionContext(addr);

            async IAsyncEnumerable<string> GetRecords(string dnsAddr)
            {
                IEnumerable<string> records = await _dns.QueryTxtAsync(dnsAddr);
                foreach (string text in records)
                {
                    const string prefix = "dnsaddr=";

                    if (text.StartsWith(prefix))
                    {
                        Multiaddress resolvedAddr = text[prefix.Length..];
                        if (resolutionContext.ExpectedPeerId is null || resolvedAddr.GetPeerId() == resolutionContext.ExpectedPeerId)
                        {
                            yield return $"{text[prefix.Length..]}{resolutionContext.Suffix}";
                        }
                    }
                }
            }

            await foreach (string item in GetRecords($"_dnsaddr.{addr.Get<DnsAddr>()}"))
            {
                await foreach (Multiaddress resolved in Resolve(item))
                {
                    yield return resolved;
                }
            }
        }
        else
        {
            if (addr.Has<WebSocketSecure>() && (addr.Has<DNS>() || addr.Has<DNS4>() || addr.Has<DNS6>()))
            {
                yield return addr;
                yield break;
            }

            bool resolved = false;
            if (addr.Has<DNS6>() || addr.Has<DNS>())
            {
                resolved = true;
                IEnumerable<IPAddress> addrs = await _dns.QueryAaaaAsync(addr.Get<DNS6>()?.ToString() ?? addr.Get<DNS>().ToString());

                foreach (IPAddress record in addrs)
                {
                    if (addr.Has<DNS6>())
                    {
                        yield return addr.Clone().Replace<DNS6, IP6>(record);
                    }
                    else
                    {
                        yield return addr.Clone().Replace<DNS, IP6>(record);
                    }
                }
            }
            if (addr.Has<DNS4>() || addr.Has<DNS>())
            {
                resolved = true;
                IEnumerable<IPAddress> addrs4 = await _dns.QueryAAsync(addr.Get<DNS4>()?.ToString() ?? addr.Get<DNS>().ToString());

                foreach (IPAddress record in addrs4)
                {
                    if (addr.Has<DNS4>())
                    {
                        yield return addr.Clone().Replace<DNS4, IP4>(record);
                    }
                    else
                    {
                        yield return addr.Clone().Replace<DNS, IP4>(record);
                    }
                }
            }

            if (!resolved)
            {
                yield return addr;
            }
        }
    }

    private static DnsaddrResolutionContext GetDnsaddrResolutionContext(Multiaddress addr)
    {
        string[] segments = addr.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        int dnsaddrIndex = Array.FindIndex(segments, p => p.Equals("dnsaddr", StringComparison.OrdinalIgnoreCase));
        if (dnsaddrIndex < 0)
        {
            return new DnsaddrResolutionContext(null, string.Empty);
        }

        int suffixStart = dnsaddrIndex + 2;
        int circuitIndex = Array.FindIndex(segments, suffixStart, p => p.Equals("p2p-circuit", StringComparison.OrdinalIgnoreCase));
        if (circuitIndex >= 0)
        {
            PeerId? relayPeerId = GetLastPeerId(segments, suffixStart, circuitIndex);
            string circuitSuffix = "/" + string.Join('/', segments.Skip(circuitIndex));
            return new DnsaddrResolutionContext(relayPeerId, circuitSuffix);
        }

        return new DnsaddrResolutionContext(GetLastPeerId(segments, suffixStart, segments.Length), string.Empty);
    }

    private static PeerId? GetLastPeerId(string[] segments, int startIndex, int endIndex)
    {
        for (int i = endIndex - 2; i >= startIndex; i--)
        {
            if (segments[i].Equals("p2p", StringComparison.OrdinalIgnoreCase))
            {
                return new PeerId(segments[i + 1]);
            }
        }

        return null;
    }

    private sealed record DnsaddrResolutionContext(PeerId? ExpectedPeerId, string Suffix);
}
