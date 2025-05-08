// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.E2eTests;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using System.Text;

namespace Libp2p.Protocols.Pubsub.E2eTests;

public class PubsubE2eTestSetup : E2eTestSetup
{
    public PubsubSettings DefaultSettings { get; set; } = new PubsubSettings { LowestDegree = 2, Degree = 3, LazyDegree = 3, HighestDegree = 4, HeartbeatInterval = 200 };
    public Dictionary<int, PubsubRouter> Routers { get; } = [];


    protected override IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
    {
        return base.ConfigureLibp2p(builder.WithPubsub());
    }

    protected override IServiceCollection ConfigureServices(IServiceCollection col)
    {
        return base.ConfigureServices(col);
    }

    protected override void AddToPrintState(StringBuilder sb, int index)
    {
        base.AddToPrintState(sb, index);
        sb.AppendLine(Routers[index].ToString());
    }

    protected override void AddAt(int index)
    {
        base.AddAt(index);
        Routers[index] = ServiceProviders[index].GetService<PubsubRouter>()!;
        _ = Routers[index].StartAsync(Peers[index]);
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

        CancellationTokenSource cts = new(timeoutMs);

        while (true)
        {
            cts.Token.ThrowIfCancellationRequested();

            bool stillWaiting = false;

            foreach (IRoutingStateContainer router in Routers.Values)
            {
                if (router.Mesh[topic].Count < requiredCount)
                {
                    stillWaiting = true;
                    break;
                }
            }

            PrintState();

            if (!stillWaiting) break;
            await Task.Delay(1000, cts.Token);
        }
    }

    public async Task WaitForBrokenMeshAsync(string topic, int timeoutMs = 15_000)
    {
        int requiredCount = int.Min(Routers.Count - 1, DefaultSettings.LowestDegree);

        CancellationTokenSource cts = new(timeoutMs);

        while (true)
        {
            PrintState();

            cts.Token.ThrowIfCancellationRequested();

            foreach (IRoutingStateContainer router in Routers.Values)
            {
                if (router.Mesh[topic].Count < requiredCount)
                {
                    return;
                }
            }

            await Task.Delay(1000, cts.Token);
        }
    }
}
