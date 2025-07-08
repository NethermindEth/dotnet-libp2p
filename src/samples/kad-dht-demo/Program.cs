// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Libp2p.Core;
using Libp2p.Protocols.KadDht;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;

namespace KadDhtDemo
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("KadDHT Demo");
            Console.WriteLine("===========");
            
            // Create a host with libp2p services
            var host = CreateHostBuilder(args).Build();
            
            // Get the peer factory
            var peerFactory = host.Services.GetRequiredService<IPeerFactory>();
            
            // Create a local peer
            using var localPeer = peerFactory.Create();
            
            // Start listening
            await localPeer.StartListenAsync();
            
            Console.WriteLine($"Local peer ID: {localPeer.Identity.PeerId}");
            Console.WriteLine($"Listening on: {string.Join(", ", localPeer.Addresses)}");
            
            // Process commands
            while (true)
            {
                Console.WriteLine("\nAvailable commands:");
                Console.WriteLine("  connect <multiaddr> - Connect to a peer");
                Console.WriteLine("  put <key> <value> - Store a value in the DHT");
                Console.WriteLine("  get <key> - Retrieve a value from the DHT");
                Console.WriteLine("  provide <key> - Announce that you provide a key");
                Console.WriteLine("  find-providers <key> - Find providers for a key");
                Console.WriteLine("  find-peers <key> - Find peers closest to a key");
                Console.WriteLine("  exit - Exit the application");
                
                Console.Write("\n> ");
                var input = Console.ReadLine()?.Trim();
                
                if (string.IsNullOrEmpty(input))
                    continue;
                
                var parts = input.Split(' ', 3);
                var command = parts[0].ToLowerInvariant();
                
                try
                {
                    switch (command)
                    {
                        case "connect":
                            if (parts.Length < 2)
                            {
                                Console.WriteLine("Usage: connect <multiaddr>");
                                continue;
                            }
                            await ConnectToPeerAsync(localPeer, parts[1]);
                            break;
                            
                        case "put":
                            if (parts.Length < 3)
                            {
                                Console.WriteLine("Usage: put <key> <value>");
                                continue;
                            }
                            await PutValueAsync(host.Services, localPeer, parts[1], parts[2]);
                            break;
                            
                        case "get":
                            if (parts.Length < 2)
                            {
                                Console.WriteLine("Usage: get <key>");
                                continue;
                            }
                            await GetValueAsync(host.Services, localPeer, parts[1]);
                            break;
                            
                        case "provide":
                            if (parts.Length < 2)
                            {
                                Console.WriteLine("Usage: provide <key>");
                                continue;
                            }
                            await ProvideKeyAsync(host.Services, localPeer, parts[1]);
                            break;
                            
                        case "find-providers":
                            if (parts.Length < 2)
                            {
                                Console.WriteLine("Usage: find-providers <key>");
                                continue;
                            }
                            await FindProvidersAsync(host.Services, localPeer, parts[1]);
                            break;
                            
                        case "find-peers":
                            if (parts.Length < 2)
                            {
                                Console.WriteLine("Usage: find-peers <key>");
                                continue;
                            }
                            await FindPeersAsync(host.Services, localPeer, parts[1]);
                            break;
                            
                        case "exit":
                            return;
                            
                        default:
                            Console.WriteLine($"Unknown command: {command}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
        }
        
        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    logging.SetMinimumLevel(LogLevel.Information);
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddLibp2p(builder =>
                    {
                        // Add KadDht protocol
                        builder.AddKadDht(options =>
                        {
                            options.EnableServerMode = true;
                            options.EnableClientMode = true;
                            options.EnableValueStorage = true;
                            options.EnableProviderStorage = true;
                            options.BucketSize = 20;
                            options.Alpha = 3;
                        });
                    });
                });
        
        private static async Task ConnectToPeerAsync(ILocalPeer localPeer, string multiaddr)
        {
            Console.WriteLine($"Connecting to {multiaddr}...");
            
            var remotePeer = await localPeer.DialAsync(multiaddr);
            
            Console.WriteLine($"Connected to peer {remotePeer.RemotePeer.Id}");
        }
        
        private static async Task PutValueAsync(IServiceProvider services, ILocalPeer localPeer, string key, string value)
        {
            Console.WriteLine($"Putting value for key '{key}'...");
            
            var contentRouter = services.GetRequiredService<IContentRouter>();
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            
            // Use the content router to store the value
            await contentRouter.PutValueAsync(keyBytes, valueBytes);
            
            Console.WriteLine("Value stored successfully");
        }
        
        private static async Task GetValueAsync(IServiceProvider services, ILocalPeer localPeer, string key)
        {
            Console.WriteLine($"Getting value for key '{key}'...");
            
            var contentRouter = services.GetRequiredService<IContentRouter>();
            var keyBytes = Encoding.UTF8.GetBytes(key);
            
            // Use the content router to get the value
            var value = await contentRouter.GetValueAsync(keyBytes);
            
            if (value != null)
            {
                Console.WriteLine($"Value: {Encoding.UTF8.GetString(value)}");
            }
            else
            {
                Console.WriteLine("Value not found");
            }
        }
        
        private static async Task ProvideKeyAsync(IServiceProvider services, ILocalPeer localPeer, string key)
        {
            Console.WriteLine($"Announcing provider for key '{key}'...");
            
            var contentRouter = services.GetRequiredService<IContentRouter>();
            var keyBytes = Encoding.UTF8.GetBytes(key);
            
            // Use the content router to announce that we provide the key
            await contentRouter.ProvideAsync(keyBytes);
            
            Console.WriteLine("Provider announced successfully");
        }
        
        private static async Task FindProvidersAsync(IServiceProvider services, ILocalPeer localPeer, string key)
        {
            Console.WriteLine($"Finding providers for key '{key}'...");
            
            var contentRouter = services.GetRequiredService<IContentRouter>();
            var keyBytes = Encoding.UTF8.GetBytes(key);
            
            // Use the content router to find providers
            var providers = await contentRouter.FindProvidersAsync(keyBytes);
            
            if (providers.Any())
            {
                Console.WriteLine($"Found {providers.Count()} providers:");
                foreach (var provider in providers)
                {
                    Console.WriteLine($"  {provider}");
                }
            }
            else
            {
                Console.WriteLine("No providers found");
            }
        }
        
        private static async Task FindPeersAsync(IServiceProvider services, ILocalPeer localPeer, string key)
        {
            Console.WriteLine($"Finding peers closest to key '{key}'...");
            
            // Get the KadDhtProtocol instance
            var kadDhtProtocol = services.GetRequiredService<KadDhtProtocol>();
            var keyBytes = Encoding.UTF8.GetBytes(key);
            
            // Convert the key to ValueHash256
            var keyOperator = services.GetRequiredService<PeerIdKeyOperator>();
            var keyHash = keyOperator.GetKeyHash(keyBytes);
            
            // Use the Kademlia implementation to find closest peers
            var kademlia = services.GetRequiredService<IKademlia<PeerId, ValueHash256>>();
            var peers = await kademlia.LookupNodesClosest(keyHash, CancellationToken.None);
            
            if (peers.Any())
            {
                Console.WriteLine($"Found {peers.Length} peers:");
                foreach (var peer in peers)
                {
                    Console.WriteLine($"  {peer}");
                }
            }
            else
            {
                Console.WriteLine("No peers found");
            }
        }
    }
} 