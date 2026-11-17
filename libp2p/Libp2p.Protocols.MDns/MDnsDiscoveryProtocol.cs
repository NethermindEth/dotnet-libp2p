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

        try
        {
            PeerName = RandomString(32);
            ServiceProfile service = new(PeerName, ServiceNameForLibrary, 0);

            service.Resources.RemoveAll(r => r is TXTRecord);

            IPAddress[] hostAddresses = HostAddresses();
            List<string> advertised = localPeerAddrs
                .SelectMany(addr => AddressesToAdvertise(addr, hostAddresses))
                .Select(addr => $"dnsaddr={addr}")
                .Distinct()
                .ToList();

            if (advertised.Count == 0)
            {
                // Advertising nothing is indistinguishable from advertising a service with no way
                // to reach it, and the query loop below would still make this node look healthy.
                _logger?.LogWarning("No routable address among {0}; this peer will not be discoverable",
                    string.Join(",", localPeerAddrs));
            }
            else
            {
                // One address per record. Makaretu truncates an oversized response by dropping
                // whole records, so a single record holding every address loses all of them at
                // once, while separate records lose only the ones that did not fit.
                foreach (string entry in advertised)
                {
                    service.Resources.Add(new TXTRecord()
                    {
                        Name = service.FullyQualifiedName,
                        Strings = [entry],
                    });
                }
            }

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

        _run = RunAsync(sd, lifetime.Token);
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

    private async Task RunAsync(ServiceDiscovery sd, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger?.LogTrace("Querying {0}", ServiceNameForLibrary);
                sd.QueryServiceInstances(ServiceNameForLibrary);
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
    internal static IPAddress[] HostAddresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => !IPAddress.IsLoopback(address))];

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
