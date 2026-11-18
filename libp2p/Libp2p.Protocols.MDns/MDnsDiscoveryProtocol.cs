// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using Nethermind.Libp2p.Core.Discovery;
using Makaretu.Dns;
using Multiformats.Address;
using Multiformats.Address.Net;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Libp2p.E2eTests")]

namespace Nethermind.Libp2p.Protocols;

public class MDnsDiscoveryProtocol(PeerStore peerStore, ILoggerFactory? loggerFactory = null, MDnsDiscoverySettings? settings = null)
    : IDiscoveryProtocol, IDisposable, IAsyncDisposable
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<MDnsDiscoveryProtocol>();
    private const int MdnsQueryInterval = 5000;

    private readonly MDnsDiscoverySettings _settings = settings ?? new();

    /// <summary>
    /// The service name as the mDNS library wants it. ServiceProfile and QueryServiceInstances
    /// append the mDNS domain themselves, so a name that already carries it would be advertised and
    /// queried as <c>_p2p._udp.local.local</c>, which no standard peer looks for. The setting stays
    /// in the specification's own form and is normalized here, for both paths.
    /// </summary>
    private string ServiceNameForLibrary => WithoutMDnsDomain(_settings.ServiceName);

    private string PeerName = null!;

    private readonly object _lifecycleLock = new();
    private bool _disposed;
    private CancellationTokenSource? _lifetime;
    private ServiceDiscovery? _serviceDiscovery;
    private Task _run = Task.CompletedTask;

    /// <summary>
    /// Advertises the local peer and periodically queries the network for other peers.
    /// Discovery stops when <paramref name="token"/> is cancelled or when it is disposed; the peer store is not disposed.
    /// </summary>
    public Task StartDiscoveryAsync(IReadOnlyList<Multiaddress> localPeerAddrs, CancellationToken token = default)
    {
        ObservableCollection<Multiaddress> peers = [];
        string? localPeerId = localPeerAddrs.First().GetPeerId()?.ToString();

        if (localPeerId is null)
        {
            throw new Libp2pException("Peer address lacks peer id");
        }

        ServiceDiscovery sd;
        CancellationTokenSource lifetime;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_lifetime is not null)
            {
                throw new InvalidOperationException("Discovery has been already started");
            }

            sd = _serviceDiscovery = new();
            lifetime = _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        // Normalized once, so the profile, the receive filter and the query loop all talk about the
        // same service even if the setting is mutated afterwards.
        string serviceName = ServiceNameForLibrary;

        try
        {
            PeerName = RandomString(32);

            // One address source for both halves of the response. Left to itself, ServiceProfile
            // fills the A and AAAA records from MulticastService.GetLinkLocalAddresses, which omits
            // every ULA and global IPv6 address -- so the records the specification's "find all
            // response" calls for would advertise a narrower set than the dnsaddr entries beside
            // them, and a tool that queries <host-name> AAAA directly, as the Gotchas section says
            // many do, would only ever learn a link-local address.
            IPAddress[] hostAddresses = HostAddresses();
            ServiceProfile service = new(PeerName, serviceName, 0,
                [.. hostAddresses.Select(WithoutScopeId).Distinct()]);
            DomainName qualifiedServiceName = service.QualifiedServiceName;

            service.Resources.RemoveAll(r => r is TXTRecord);

            List<string> advertised = BoundedAdvertisement(
                localPeerAddrs.SelectMany(addr => AddressesToAdvertise(addr, hostAddresses)), _logger);

            if (advertised.Count == 0)
            {
                // Advertising nothing is indistinguishable from advertising a service with no way
                // to reach it, and the query loop below would still make this node look healthy.
                _logger?.LogWarning("No routable address among {0}; this peer will not be discoverable",
                    string.Join(",", localPeerAddrs));
            }
            else
            {
                // One record holding every address. go-libp2p's resolver reads its addresses from
                // go-libp2p's mDNS client, which assigns rather than appends per TXT record
                // (`entries[rr.Hdr.Name].Text = rr.Txt`), so separate records leave a Go peer with
                // only the last one. Grouping is safe because BoundedAdvertisement keeps the set
                // inside the response size limit, which is what made grouping lossy before.
                service.Resources.Add(new TXTRecord()
                {
                    Name = service.FullyQualifiedName,
                    Strings = advertised,
                });
            }

            // Both halves are in now, so the whole response can be brought inside the limit.
            TrimToResponseLimit(service, _logger);

            _logger?.LogTrace("DNS records to share: {0}", string.Join(",", service.Resources));
            _logger?.LogInformation("Started as {0} {1}", PeerName, service.QualifiedServiceName);

            sd.ServiceDiscovered += (s, serviceName) =>
            {
                _logger?.LogTrace("Srv disc {0}", serviceName);
            };

            sd.ServiceInstanceDiscovered += (s, e) =>
            {
                if (e.ServiceInstanceName.ToString().Contains(PeerName))
                {
                    return;
                }

                // ServiceName promises that a custom name confines discovery to peers using it, and
                // nothing upstream enforces that: Makaretu raises this event for any local-domain
                // service PTR without matching it against the query. Reading dnsaddr records from
                // the answer section made that reachable, because a standard peer's answer-section
                // records would otherwise be accepted by a node configured for its own group.
                if (!IsFromService(e.Message, qualifiedServiceName, e.ServiceInstanceName))
                {
                    _logger?.LogTrace("Ignoring {0}, not an instance of {1}", e.ServiceInstanceName, qualifiedServiceName);
                    return;
                }

                // Decoded one at a time on purpose. A standard peer advertises every transport it
                // listens on and this build does not know all of them -- go-libp2p enables
                // WebTransport by default, and decoding that address throws NotSupportedException.
                // Decoding the set in one expression would lose the peer's usable TCP and QUIC
                // addresses along with the one that could not be read.
                List<Multiaddress> records = DecodeAdvertised(
                    AdvertisedStrings(e.Message, e.ServiceInstanceName), _logger);

                _logger?.LogTrace("Inst disc {0}, msg: {1}", e.ServiceInstanceName, e.Message);

                // Taken from the addresses that decoded, rather than from the first one, which may
                // have been the unreadable entry or may carry no peer id at all.
                PeerId? remotePeerId = records.Select(record => record.GetPeerId()).FirstOrDefault(id => id is not null);
                if (remotePeerId is not null && localPeerId != remotePeerId.ToString() && !peers.Contains(records[0]))
                {
                    foreach (Multiaddress peer in records)
                    {
                        peers.Add(peer);
                    }
                    peerStore.Discover([.. records], isTrusted: false);
                }
            };

            sd.Advertise(service);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error setting up mDNS");
        }

        _run = RunAsync(sd, serviceName, lifetime.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops querying, sends a goodbye for the advertised service and releases the mDNS sockets,
    /// without waiting for the query loop to finish.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? lifetime;
        ServiceDiscovery? sd;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            lifetime = _lifetime;
            sd = _serviceDiscovery;
        }

        lifetime?.Cancel();
        lifetime?.Dispose();

        if (sd is not null)
        {
            try
            {
                sd.Unadvertise();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Unable to send mDNS goodbye");
            }
            sd.Dispose();
        }
    }

    /// <summary>
    /// Stops discovery like <see cref="Dispose"/> and waits for the query loop to finish.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();

        await _run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task RunAsync(ServiceDiscovery sd, string serviceName, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger?.LogTrace("Querying {0}", serviceName);
                sd.QueryServiceInstances(serviceName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error querying network");
            }
            await Task.Delay(MdnsQueryInterval, token);
        }
    }

    /// <summary>
    /// This host's usable unicast addresses, for rewriting a wildcard listener.
    /// <para>
    /// Not <c>MulticastService.GetLinkLocalAddresses</c>: that returns an IPv6 address only when it
    /// is link-local, so an IPv6 wildcard would advertise <c>fe80::</c> endpoints alone. go-libp2p
    /// rejects those in <c>Swarm.CanDial</c> before dialing, which leaves an IPv6-only listener
    /// unreachable even when its interface has a ULA or global address that works.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Makaretu's own selection, so this follows the interfaces it actually sends on. That matters
    /// in both directions: it enumerates every unicast address of the chosen interfaces, including
    /// the ULA and global IPv6 ones <c>GetLinkLocalAddresses</c> omits, and it falls back to the
    /// operational loopback interfaces when the host has no operational non-loopback one. Filtering
    /// loopback out unconditionally left such a host advertising nothing, while Makaretu still
    /// selected loopback for multicast, so local peers could reach it but never learn how.
    /// </remarks>
    internal static IPAddress[] HostAddresses() => [.. MulticastService.GetIPAddresses()];

    /// <summary>
    /// The addresses to advertise for one listen address: itself when it names a concrete IP, and
    /// this host's addresses of the same family when it is a wildcard. Both families are handled,
    /// and a wildcard that expands to nothing is dropped rather than advertised as-is.
    /// </summary>
    internal static IEnumerable<Multiaddress> AddressesToAdvertise(Multiaddress addr, IReadOnlyList<IPAddress> hostAddresses)
    {
        // dns*, relay and memory addresses carry no IP layer, and ToEndPoint reports 0.0.0.0 for
        // them -- which must not be mistaken for a wildcard listener and expanded to our own IPs.
        if (!addr.Has<IP4>() && !addr.Has<IP6>())
        {
            yield break;
        }

        int ipIndex = addr.Protocols.FindIndex(protocol => protocol is IP4 or IP6);
        IPEndPoint endpoint = addr.ToEndPoint(out ProtocolType proto);
        if (!endpoint.Address.Equals(IPAddress.Any) && !endpoint.Address.Equals(IPAddress.IPv6Any))
        {
            // A concrete listener can carry a scope id too, since listen addresses are derived from
            // interface addresses. The same normalization the wildcard path applies is needed here.
            yield return WithAddress(addr, ipIndex, WithoutScopeId(endpoint.Address));
            yield break;
        }

        // A wildcard needs a transport layer to be routable once rewritten; /ip4/0.0.0.0 on its own
        // reports ProtocolType.Unknown and port 0, and swapping its IP would not fix that.
        if (proto == ProtocolType.Unknown)
        {
            yield break;
        }

        foreach (IPAddress hostAddress in hostAddresses)
        {
            if (hostAddress.AddressFamily != endpoint.Address.AddressFamily)
            {
                continue;
            }

            yield return WithAddress(addr, ipIndex, WithoutScopeId(hostAddress));
        }
    }

    /// <summary>A TXT string may not exceed 255 bytes; RFC 6763 section 6.1.</summary>
    private const int MaxTxtStringLength = 255;

    /// <summary>
    /// Byte budget for the dnsaddr strings of one response, with headroom under the 8,952-byte
    /// limit for the SRV, A, AAAA and PTR records sharing the message. Measured: 93 typical
    /// entries or 46 webrtc-direct ones fit in a response, so this bound is never reached by a
    /// real host and exists so that it cannot be.
    /// </summary>
    private const int MaxAdvertisedBytes = 7000;

    /// <summary>
    /// The response size MulticastService truncates at, by dropping whole records.
    /// </summary>
    private const int MaxResponseBytes = 8952;

    /// <summary>
    /// Drops the least reachable host-address records until the whole response fits.
    /// <para>
    /// The response has two halves and bounding only the TXT strings is not enough.
    /// <c>ServiceProfile</c> turns every host address into an A or AAAA record, so a host with many
    /// addresses overflows the response even when the dnsaddr set is inside its own budget -- and
    /// the sender truncates by dropping whole records, taking the grouped TXT record, the only part
    /// carrying dial addresses, with it.
    /// </para>
    /// <para>
    /// Measured on the assembled message rather than estimated per record, because name
    /// compression makes a record's cost depend on the rest of the message -- the fixed headroom
    /// this replaces was an estimate, and that is exactly how it went wrong. Link-local records go
    /// first, matching the order the dnsaddr entries are offered in.
    /// </para>
    /// </summary>
    internal static void TrimToResponseLimit(ServiceProfile service, ILogger? logger = null)
    {
        int dropped = 0;
        while (ResponseBytes(service) > MaxResponseBytes)
        {
            AddressRecord? least = service.Resources
                .OfType<AddressRecord>()
                .OrderByDescending(record => IsLinkLocal(record.Address))
                .FirstOrDefault();

            if (least is null)
            {
                break;
            }

            service.Resources.Remove(least);
            dropped++;
        }

        if (dropped != 0)
        {
            logger?.LogWarning("{0} of this host's address records did not fit the mDNS response; " +
                "the dnsaddr entries peers dial from were kept", dropped);
        }
    }

    /// <remarks>
    /// The reply carries more than the profile's own resources: <c>ServiceDiscovery.Advertise</c>
    /// catalogues a service-to-instance PTR of its own, and that is the record a browsing peer's
    /// query is answered with. Leaving it out of the sizing under-counts the reply, which measured
    /// 8,959 bytes against the 8,952-byte limit on a response this method called a fit.
    /// </remarks>
    private static int ResponseBytes(ServiceProfile service)
    {
        Message message = new();
        message.Answers.Add(new PTRRecord
        {
            Name = service.QualifiedServiceName,
            DomainName = service.FullyQualifiedName,
        });

        foreach (ResourceRecord record in service.Resources)
        {
            message.AdditionalRecords.Add(record);
        }

        return message.ToByteArray().Length;
    }

    /// <summary>
    /// The dnsaddr strings to advertise, in one record and within the response size limit.
    /// <para>
    /// Both constraints have to hold at once. Separate records lose all but one address at a Go
    /// peer, because that client assigns the instance's text per record rather than appending to it;
    /// a single record that overflows the response is dropped whole by the sender, losing all of
    /// them. So they go in one record and the set is bounded.
    /// </para>
    /// <para>
    /// Routable addresses are offered before link-local ones, so that if the bound ever does bite
    /// it drops the addresses a peer is least likely to reach: go-libp2p rejects link-local in
    /// <c>Swarm.CanDial</c>, while js-libp2p advertises nothing else.
    /// </para>
    /// </summary>
    internal static List<string> BoundedAdvertisement(IEnumerable<Multiaddress> addresses, ILogger? logger = null)
    {
        List<string> entries = [];
        int used = 0;
        int dropped = 0;

        // OrderBy is stable, so listen order survives inside each group.
        foreach (Multiaddress address in addresses.OrderBy(IsLinkLocal))
        {
            string entry = $"dnsaddr={address}";
            if (entries.Contains(entry))
            {
                continue;
            }

            if (entry.Length > MaxTxtStringLength || used + entry.Length > MaxAdvertisedBytes)
            {
                dropped++;
                continue;
            }

            entries.Add(entry);
            used += entry.Length;
        }

        if (dropped != 0)
        {
            logger?.LogWarning("{0} of this peer's addresses did not fit the mDNS response and are not advertised", dropped);
        }

        return entries;
    }

    private static bool IsLinkLocal(Multiaddress address) =>
        address.Protocols.FirstOrDefault(protocol => protocol is IP4 or IP6)?.Value is IPAddress ip
        && IsLinkLocal(ip);

    private static bool IsLinkLocal(IPAddress ip) =>
        ip.IsIPv6LinkLocal || (ip.AddressFamily == AddressFamily.InterNetwork
            && ip.GetAddressBytes() is [169, 254, ..]);

    /// <summary>
    /// Whether the message actually offers this instance under the service we are configured for.
    /// <para>
    /// A positive TTL is required because a goodbye packet announces the same instance with TTL
    /// zero, and importing a peer from its own withdrawal would keep a departed peer discoverable.
    /// </para>
    /// </summary>
    internal static bool IsFromService(Message message, DomainName serviceName, DomainName instanceName) =>
        message.Answers.Concat(message.AdditionalRecords)
            .OfType<PTRRecord>()
            .Any(ptr => ptr.TTL > TimeSpan.Zero
                && ptr.Name == serviceName
                && ptr.DomainName == instanceName);

    /// <summary>
    /// The TXT strings a peer advertised for one service instance, from both record sections.
    /// <para>
    /// Implementations differ on where they put them: this one and go-libp2p answer with the TXT
    /// records in the additional section, while js-libp2p puts the PTR and every <c>dnsaddr</c> TXT
    /// record in the answer section. Reading only one section discovers such a peer and then drops
    /// it, because the decoded address list comes back empty.
    /// </para>
    /// <para>
    /// Restricted to the discovered instance, since one message can carry records for several.
    /// </para>
    /// </summary>
    internal static IEnumerable<string> AdvertisedStrings(Message message, DomainName instanceName)
    {
        return message.Answers.Concat(message.AdditionalRecords)
            .OfType<TXTRecord>()
            .Where(record => IsSameName(record.Name, instanceName))
            .SelectMany(record => record.Strings);
    }

    /// <summary>DNS names are case-insensitive, and a root label does not change which name it is.</summary>
    private static bool IsSameName(DomainName? left, DomainName? right) =>
        string.Equals(left?.ToString().TrimEnd('.'), right?.ToString().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The addresses from a peer's TXT strings, decoded one at a time.
    /// <para>
    /// A standard peer advertises every transport it listens on and this build does not know all of
    /// them -- go-libp2p enables WebTransport by default, and decoding that address throws
    /// <see cref="NotSupportedException"/>. Decoding the set in one expression would lose the peer's
    /// usable TCP and QUIC addresses along with the one entry that could not be read.
    /// </para>
    /// </summary>
    internal static List<Multiaddress> DecodeAdvertised(IEnumerable<string> txtStrings, ILogger? logger = null)
    {
        List<Multiaddress> records = [];
        foreach (string entry in txtStrings.Where(text => text.StartsWith("dnsaddr=")))
        {
            try
            {
                records.Add(Multiaddress.Decode(entry["dnsaddr=".Length..]));
            }
            catch (Exception ex)
            {
                logger?.LogTrace(ex, "Ignoring an mDNS address this build cannot read: {0}", entry);
            }
        }

        return records;
    }

    /// <summary>
    /// A scope id is this host's own interface index and means nothing to the receiver; go-multiaddr
    /// rejects a scoped IPv6 string as a multiaddress outright.
    /// </summary>
    private static IPAddress WithoutScopeId(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address;

    /// <summary>
    /// Clones the address and swaps only its IP, so the transport, port, <c>/quic-v1</c> and
    /// <c>/p2p</c> parts survive.
    /// <para>
    /// <c>ReplaceOrAdd</c> cannot do this: it mutates its receiver -- which here would rewrite the
    /// caller's own listen addresses -- and for <c>IP6</c> it drops the existing entry and appends
    /// the replacement at the end, producing an address that is not routable.
    /// </para>
    /// </summary>
    private static Multiaddress WithAddress(Multiaddress addr, int ipIndex, IPAddress address)
    {
        Multiaddress rewritten = addr.Clone();
        rewritten.Protocols[ipIndex] = address.AddressFamily == AddressFamily.InterNetworkV6
            ? new IP6(address)
            : new IP4(address);
        return rewritten;
    }

    /// <summary>
    /// Strips the mDNS domain, and a root label, from a service name. <c>_p2p._udp.local</c> and
    /// <c>_p2p._udp</c> both become <c>_p2p._udp</c>, so either form of a configured name reaches
    /// the wire as the same DNS-SD service.
    /// </summary>
    internal static string WithoutMDnsDomain(string serviceName)
    {
        string trimmed = serviceName.TrimEnd('.');
        return trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^".local".Length]
            : trimmed;
    }

    private static string RandomString(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(length, Random.Shared,
            (chars, rand) =>
            {
                for (int i = 0; i < chars.Length; i++)
                    chars[i] = alphabet[rand.Next(0, alphabet.Length)];
            });
    }
}
