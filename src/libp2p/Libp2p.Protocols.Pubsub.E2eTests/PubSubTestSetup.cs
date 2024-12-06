using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using System.Text;

namespace Libp2p.Protocols.Pubsub.E2eTests;


public class TestRequestResponseProtocol : ISessionProtocol<int, int>
{
    public string Id => "1";

    public async Task<int> DialAsync(IChannel downChannel, ISessionContext context, int request)
    {
        await downChannel.WriteVarintAsync(request);
        return await downChannel.ReadVarintAsync();
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        var request = await downChannel.ReadVarintAsync();
        await downChannel.WriteVarintAsync(request + 1);
    }
}

public class PubsubTestSetup
{
    protected static TestContextLoggerFactory loggerFactory = new();
    private int Counter = 0;

    public PubsubSettings DefaultSettings { get; set; } = new PubsubSettings { LowestDegree = 2, Degree = 3, LazyDegree = 3, HighestDegree = 4, HeartbeatInterval = 200 };
    protected ILogger testLogger { get; set; } = loggerFactory.CreateLogger("test-setup");

    public ChannelBus CommonBus { get; } = new(loggerFactory);
    public Dictionary<int, IPeer> Peers { get; } = new();
    public Dictionary<int, PeerStore> PeerStores { get; } = new();
    public Dictionary<int, PubsubRouter> Routers { get; } = new();
    public Dictionary<int, ServiceProvider> ServiceProviders { get; } = new();

    public async Task StartPeersAsync(int count, PubsubSettings? customPubsubSettings = null)
    {
        for (int i = Counter; i < Counter + count; i++)
        {
            // But we create a seprate setup for every peer
            ServiceProvider sp = ServiceProviders[i] = new ServiceCollection()
                   .AddSingleton(sp => new TestBuilder(sp)
                        .AddAppLayerProtocol<GossipsubProtocol>()
                        .AddAppLayerProtocol<GossipsubProtocolV11>()
                        .AddAppLayerProtocol<TestRequestResponseProtocol>()
                        .AddAppLayerProtocol<GossipsubProtocolV12>()
                        .AddAppLayerProtocol<FloodsubProtocol>())
                   .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
                   .AddSingleton<PubsubRouter>()
                   .AddSingleton<IProtocolStackSettings, ProtocolStackSettings>()
                   .AddSingleton<PeerStore>()
                   .AddSingleton(CommonBus)
                   .AddSingleton(sp => customPubsubSettings ?? DefaultSettings)
                   .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
                   .BuildServiceProvider();

            PeerStores[i] = ServiceProviders[i].GetService<PeerStore>()!;
            Peers[i] = sp.GetService<IPeerFactory>()!.Create(TestPeers.Identity(i));
            Routers[i] = sp.GetService<PubsubRouter>()!;

            await Peers[i].StartListenAsync([TestPeers.Multiaddr(i)]);
        }
    }

    public void StartPubsub()
    {
        foreach ((int index, PubsubRouter router) in Routers)
        {
            _ = router.RunAsync(Peers[index]);
        }
    }

    /// <summary>
    /// Manual heartbeat in case the period is set to infinite
    /// </summary>
    /// <returns></returns>
    public async Task Heartbeat()
    {
        foreach (PubsubRouter router in Routers.Values)
        {
            await router.Heartbeat();
        }
    }

    private int stateCounter = 1;

    public void PrintState(bool outputToConsole = false)
    {
        StringBuilder reportBuilder = new();
        reportBuilder.AppendLine($"Test state#{stateCounter++}");

        foreach ((int index, PubsubRouter router) in Routers)
        {
            reportBuilder.AppendLine(router.ToString());
            reportBuilder.AppendLine(Peers[index].ToString());
            reportBuilder.AppendLine();
        }

        string report = reportBuilder.ToString();

        if (outputToConsole)
        {
            Console.WriteLine(report);
        }
        else
        {
            testLogger.LogInformation(report.ToString());
        }
    }

    public void Subscribe(string topic)
    {
        foreach (PubsubRouter router in Routers.Values)
        {
            router.GetTopic(topic);
        }
    }

    public async Task WaitForFullMeshAsync(string topic, int timeoutMs = 15_000)
    {
        int requiredCount = int.Min(Routers.Count - 1, DefaultSettings.LowestDegree);

        CancellationTokenSource cts = new();
        Task delayTask = Task.Delay(timeoutMs).ContinueWith((t) => cts.Cancel());

        while (true)
        {
            if (cts.IsCancellationRequested)
            {
                PrintState();
                throw new Exception("Timeout waiting for the network");
            }
            PrintState();

            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(100);

            bool stillWaiting = false;

            foreach (IRoutingStateContainer router in Routers.Values)
            {
                if (router.Mesh[topic].Count < requiredCount)
                {
                    stillWaiting = true;
                }
            }

            if (!stillWaiting) break;
        }
    }
}
