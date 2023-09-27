// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using Nethermind.Libp2p.Core.Discovery;
using Makaretu.Dns;

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

    public Func<Core.Multiaddr[], bool>? OnAddPeer { get; set; }
    public Func<Core.Multiaddr[], bool>? OnRemovePeer { get; set; }

    private string PeerName = null!;

    public async Task DiscoverAsync(Core.Multiaddr localPeerAddr, CancellationToken token = default)
    {
        ObservableCollection<Core.Multiaddr> peers = new();

        try
        {
            PeerName = RandomString(32);
            ServiceProfile service = new(PeerName, ServiceNameOverride ?? ServiceName, 0);

            if (localPeerAddr.At(Core.Enums.Multiaddr.Ip4) == "0.0.0.0")
            {
                service.Resources.Add(new TXTRecord()
                {
                    Name = service.FullyQualifiedName,
                    Strings = new List<string>(MulticastService.GetLinkLocalAddresses()
                        .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
                        .Select(item => $"dnsaddr={localPeerAddr.Replace(Core.Enums.Multiaddr.Ip4, item.ToString())}"))
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
                Core.Multiaddr[] records = e.Message.AdditionalRecords.OfType<TXTRecord>()
                    .Select(x => x.Strings.Where(x => x.StartsWith("dnsaddr")))
                    .SelectMany(x => x).Select(x => new Core.Multiaddr(x.Replace("dnsaddr=", ""))).ToArray();
                _logger?.LogTrace("Inst disc {0}, nmsg: {1}", e.ServiceInstanceName, e.Message);
                if (Enumerable.Any(records) && !peers.Contains(Enumerable.First(records)) && localPeerAddr.At(Core.Enums.Multiaddr.P2p) != Enumerable.First(records).At(Core.Enums.Multiaddr.P2p))
                {
                    List<string> peerAddresses = new();
                    foreach (Core.Multiaddr peer in records)
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
                for (var i = 0; i < chars.Length; i++)
                    chars[i] = alphabet[rand.Next(0, alphabet.Length)];
            });
    }
}
