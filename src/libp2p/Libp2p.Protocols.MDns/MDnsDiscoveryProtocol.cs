// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using Nethermind.Libp2p.Core.Discovery;
using Makaretu.Dns;
using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Protocols;

public class MDnsDiscoveryProtocol(PeerStore peerStore, ILoggerFactory? loggerFactory = null) : IDiscoveryProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<MDnsDiscoveryProtocol>();

    public static string Id => "mdns";

    private const string ServiceName = "_p2p._udp.local";

    private const string? ServiceNameOverride = "pubsub-chat-example";

    private string PeerName = null!;

    public async Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
    {
        ObservableCollection<Multiaddress> peers = [];
        ServiceDiscovery sd = new();

        try
        {
            PeerName = RandomString(32);
            ServiceProfile service = new(PeerName, ServiceNameOverride ?? ServiceName, 0);

            if (localPeerAddr.Get<IP4>().ToString() == "0.0.0.0")
            {
                service.Resources.Add(new TXTRecord()
                {
                    Name = service.FullyQualifiedName,
                    Strings = new List<string>(MulticastService.GetLinkLocalAddresses()
                        .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
                        .Select(item => $"dnsaddr={localPeerAddr.ReplaceOrAdd<IP4>(item.ToString())}"))
                });
            }
            else
            {
                service.Resources.Add(new TXTRecord()
                {
                    Name = service.FullyQualifiedName,
                    Strings = [$"dnsaddr={localPeerAddr}"]
                });
            }

            _logger?.LogInformation("Started as {0} {1}", PeerName, ServiceNameOverride ?? ServiceName);



            sd.ServiceDiscovered += (s, serviceName) =>
            {
                _logger?.LogTrace("Srv disc {0}", serviceName);
            };
            sd.ServiceInstanceDiscovered += (s, e) =>
            {
                Multiaddress[] records = e.Message.AdditionalRecords.OfType<TXTRecord>()
                    .Select(x => x.Strings.Where(x => x.StartsWith("dnsaddr")))
                    .SelectMany(x => x).Select(x => Multiaddress.Decode(x.Replace("dnsaddr=", ""))).ToArray();
                _logger?.LogTrace("Inst disc {0}, nmsg: {1}", e.ServiceInstanceName, e.Message);
                if (Enumerable.Any(records) && !peers.Contains(Enumerable.First(records)) && localPeerAddr.Get<P2P>().ToString() != Enumerable.First(records).Get<P2P>().ToString())
                {
                    List<string> peerAddresses = new();
                    foreach (Multiaddress peer in records)
                    {
                        peers.Add(peer);
                    }
                    peerStore.Discover(records);
                }
            };

            sd.Advertise(service);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error setting up mDNS");
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger?.LogTrace("Querying {0}", ServiceNameOverride ?? ServiceName);
                sd.QueryServiceInstances(ServiceNameOverride ?? ServiceName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error setting up mDNS");
            }
            await Task.Delay(5000, token);
        }

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
