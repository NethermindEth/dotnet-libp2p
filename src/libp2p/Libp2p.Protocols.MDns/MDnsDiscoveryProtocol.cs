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

public class MDnsDiscoveryProtocol : IDiscoveryProtocol
{
    private readonly ILogger? _logger;

    public MDnsDiscoveryProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<MDnsDiscoveryProtocol>();
    }

    public string Id => "mdns";

    private string ServiceName = "_p2p._udp.local";

    private string? ServiceNameOverride = "pubsub-chat-example";

    public Func<Multiaddress[], bool>? OnAddPeer { get; set; }
    public Func<Multiaddress[], bool>? OnRemovePeer { get; set; }

    private string PeerName = null!;

    public async Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
    {
        ObservableCollection<Multiaddress> peers = new();

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
                    Strings = new List<string>
                    {
                        $"dnsaddr={localPeerAddr}"
                    }
                });
            }

            _logger?.LogInformation("Started as {0} {1}", PeerName, ServiceNameOverride ?? ServiceName);

            ServiceDiscovery sd = new();

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
                    OnAddPeer?.Invoke(records);
                }
            };

            sd.Advertise(service);

            while (!token.IsCancellationRequested)
            {
                _logger?.LogTrace("Querying {0}", ServiceNameOverride ?? ServiceName);
                sd.QueryServiceInstances(ServiceNameOverride ?? ServiceName);
                await Task.Delay(5000, token);
            }
        }
        catch
        {

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
