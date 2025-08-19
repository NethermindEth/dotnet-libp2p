// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Makaretu.Dns.Resolving;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.OpenTelemetry;
using Nethermind.Libp2p.Protocols.Pubsub;
using OpenTelemetry;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Text;

namespace Libp2p.E2eTests;

public class E2eTestSetup : IAsyncDisposable
{
    private readonly CancellationTokenSource _commonTokenSource = new();
    private TracerProvider? tracerProvider;
    ActivityTracker? activityTracker;

    public async ValueTask DisposeAsync()
    {
        _commonTokenSource.Cancel();
        _commonTokenSource.Dispose();

        foreach (ILocalPeer peer in Peers.Values)
        {
            await peer.DisposeAsync();
        }

        activityTracker?.Dispose();
        tracerProvider?.ForceFlush();
        tracerProvider?.Dispose();
    }

    protected CancellationToken Token => _commonTokenSource.Token;

    protected static TestContextLoggerFactory loggerFactory = new();
    private int _peerCounter = 0;

    protected ILogger TestLogger { get; set; } = loggerFactory.CreateLogger("test-setup");

    public Dictionary<int, ILocalPeer> Peers { get; } = [];
    public Dictionary<int, PeerStore> PeerStores { get; } = [];
    public Dictionary<int, ServiceProvider> ServiceProviders { get; } = [];

    protected virtual IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
    {
        return builder.AddProtocol<IncrementNumberTestProtocol>();
    }

    protected virtual IServiceCollection ConfigureServices(IServiceCollection col)
    {
        return col.AddTracing("test", createRootActivity: true);
    }

    protected virtual void AddToPrintState(StringBuilder sb, int index)
    {
    }

    protected virtual void AddAt(int index)
    {

    }

    public async Task AddPeersAsync(int count)
    {
        int totalCount = _peerCounter + count;

        for (; _peerCounter < totalCount; _peerCounter++)
        {
            // But we create a separate setup for every peer
            ServiceProvider sp = ServiceProviders[_peerCounter] =
                ConfigureServices(
                    new ServiceCollection()
                       .AddLibp2p(ConfigureLibp2p)
                       .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
                       .AddSingleton(sp => new PubsubSettings { ReconnectionPeriod = 3 })
                )
                   .BuildServiceProvider();

            if (tracerProvider is null)
            {
                tracerProvider = sp.GetService<TracerProvider>();
                activityTracker = new ActivityTracker();
                tracerProvider?.AddProcessor(activityTracker);
            }

            PeerStores[_peerCounter] = sp.GetService<PeerStore>()!;
            Peers[_peerCounter] = sp.GetService<IPeerFactory>()!.Create(TestPeers.Identity(_peerCounter));

            await Peers[_peerCounter].StartListenAsync(token: Token);

            AddAt(_peerCounter);

        }
    }


    private int stateCounter = 1;

    public void PrintState(bool outputToConsole = false)
    {
        StringBuilder reportBuilder = new();
        reportBuilder.AppendLine($"Test state#{stateCounter++}");

        foreach ((int index, ILocalPeer peer) in Peers.ToList())
        {
            AddToPrintState(reportBuilder, index);
            reportBuilder.AppendLine(peer.ToString());
            reportBuilder.AppendLine();
        }

        string report = reportBuilder.ToString();

        if (outputToConsole)
        {
            Console.WriteLine(report);
        }
        else
        {
            TestLogger.LogInformation(report.ToString());
        }
    }
}

internal class ActivityTracker : BaseProcessor<Activity>
{
    ConcurrentSet<Activity> acts = new();

    public override void OnStart(Activity data)
    {
        acts.Add(data);
        base.OnStart(data);
    }

    public override void OnEnd(Activity data)
    {
        acts.Remove(data);
        base.OnEnd(data);
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var activity in acts.ToArray())
        {
            activity?.Dispose();
        }

        base.Dispose(disposing);
    }

    public Activity[] All => acts.ToArray();
}
