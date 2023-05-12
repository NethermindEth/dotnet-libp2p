// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net.Sockets;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Enums;
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
        //_logger = loggerFactory?.CreateLogger<MDnsDiscoveryProtocol>();
    }

    public string Id => "mdns";

    private string ServiceName = "_p2p._udp.local";

    private string? ServiceNameOverride = "pubsub-chat-example";
    private string HostName => $"{PeerName}.p2p.local";

    public Func<MultiAddr[], bool>? OnAddPeer { get; set; }
    public Func<MultiAddr[], bool>? OnRemovePeer { get; set; }

    private string PeerName = null!;

    public async Task DiscoverAsync(MultiAddr localPeerAddr, CancellationToken token = default)
    {
        ObservableCollection<MultiAddr> peers = new();

        try
        {
            PeerName = "0000" + RandomString(28);
            ServiceProfile service = new ServiceProfile(PeerName, ServiceNameOverride ?? ServiceName, 0);

            if (localPeerAddr.At(Multiaddr.Ip4) == "0.0.0.0")
            {
                service.Resources.Add(new TXTRecord()
                {
                    Name = service.FullyQualifiedName,
                    Strings = new List<string>(MulticastService.GetLinkLocalAddresses()
                        .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
                        .Select(item => $"dnsaddr={localPeerAddr.Replace(Multiaddr.Ip4, item.ToString())}"))
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

            ServiceDiscovery sd = new ServiceDiscovery();

            sd.ServiceDiscovered += (s, serviceName) =>
            {
                _logger?.LogTrace("Srv disc {0}", serviceName);
            };
            sd.ServiceInstanceDiscovered += (s, e) =>
            {
                MultiAddr[] records = e.Message.AdditionalRecords.OfType<TXTRecord>()
                    .Select(x => x.Strings.Where(x => x.StartsWith("dnsaddr")))
                    .SelectMany(x => x).Select(x => new MultiAddr(x.Replace("dnsaddr=", ""))).ToArray();
                _logger?.LogTrace("Inst disc {0}, nmsg: {1}", e.ServiceInstanceName, e.Message);
                if (records.Any() && !peers.Contains(records.First()) && localPeerAddr.At(Multiaddr.P2p) != records.First().At(Multiaddr.P2p))
                {
                    List<string> peerAddresses = new List<string>();
                    foreach (MultiAddr peer in records)
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
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        Random random = new Random();
        return new string(Enumerable.Repeat(chars, length).Select(s => s[random.Next(s.Length)]).ToArray());
    }

    public Task BanPeer()
    {
        return Task.CompletedTask;
    }
}
