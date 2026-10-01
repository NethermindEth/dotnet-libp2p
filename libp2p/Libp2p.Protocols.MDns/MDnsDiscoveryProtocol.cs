// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using Nethermind.Libp2p.Core.Discovery;
using Makaretu.Dns;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Libp2p.Protocols;

public class MDnsDiscoveryProtocol(PeerStore peerStore, ILoggerFactory? loggerFactory = null) : IDiscoveryProtocol, IDisposable, IAsyncDisposable
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<MDnsDiscoveryProtocol>();
    private const int MdnsQueryInterval = 5000;
    private const string ServiceName = "_p2p._udp.local";

    private const string? ServiceNameOverride = "pubsub-chat-example";

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
            ServiceProfile service = new(PeerName, ServiceNameOverride ?? ServiceName, 0);

            service.Resources.RemoveAll(r => r is TXTRecord);

            foreach (Multiaddress localPeerAddr in localPeerAddrs)
            {
                if (localPeerAddr.Get<IP4>().ToString() == "0.0.0.0")
                {
                    service.Resources.Add(new TXTRecord()
                    {
                        Name = service.FullyQualifiedName,
                        Strings = new List<string>(MulticastService.GetLinkLocalAddresses()
                            .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
                            .Select(item => $"dnsaddr={localPeerAddr.ReplaceOrAdd<IP4>(item.ToString())}")),
                    });
                }
                else
                {
                    service.Resources.Add(new TXTRecord()
                    {
                        Name = service.FullyQualifiedName,
                        Strings = [$"dnsaddr={localPeerAddr}"],
                    });
                }
            }

            _logger?.LogTrace("DNS records to share: {0}", string.Join(",", service.Resources));
            _logger?.LogInformation("Started as {0} {1}", PeerName, ServiceNameOverride ?? ServiceName);

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

                Multiaddress[] records = e.Message.AdditionalRecords.OfType<TXTRecord>()
                    .Select(x => x.Strings.Where(x => x.StartsWith("dnsaddr")))
                    .SelectMany(x => x).Select(x => Multiaddress.Decode(x.Replace("dnsaddr=", ""))).ToArray();
                _logger?.LogTrace("Inst disc {0}, msg: {1}", e.ServiceInstanceName, e.Message);
                if (records.Length != 0 && !peers.Contains(records[0]) && localPeerId != records[0].Get<P2P>().ToString())
                {
                    List<string> peerAddresses = [];
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
                _logger?.LogTrace("Querying {0}", ServiceNameOverride ?? ServiceName);
                sd.QueryServiceInstances(ServiceNameOverride ?? ServiceName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error querying network");
            }
            await Task.Delay(MdnsQueryInterval, token);
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
