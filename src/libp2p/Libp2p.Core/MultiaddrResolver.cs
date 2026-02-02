// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using DnsClient;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Multiformats.Hash;

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
        Multihash? p2p = addr.Get<P2P>().Value as Multihash;

        if (addr.Has<DnsAddr>())
        {
            async IAsyncEnumerable<string> GetRecords(string dnsAddr)
            {
                var records = await _dns.QueryTxtAsync(dnsAddr);
                foreach (string text in records)
                {
                    const string prefix = "dnsaddr=";

                    if (text.StartsWith(prefix))
                    {
                        Multiaddress addr = text[prefix.Length..];
                        if (p2p is null || (addr.Has<P2P>() && addr.Get<P2P>().Value.Equals(p2p)))
                        {
                            yield return text[prefix.Length..];
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
            bool resolved = false;
            if (addr.Has<DNS6>() || addr.Has<DNS>())
            {
                resolved = true;
                var addrs = await _dns.QueryAaaaAsync(addr.Get<DNS6>()?.ToString() ?? addr.Get<DNS>().ToString());

                foreach (var record in addrs)
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
                var addrs4 = await _dns.QueryAAsync(addr.Get<DNS4>()?.ToString() ?? addr.Get<DNS>().ToString());

                foreach (var record in addrs4)
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
}
