// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.Pubsub;
using NUnit.Framework;
using System.Collections.Concurrent;

namespace Libp2p.Protocols.Pubsub.E2eTests;

[TestFixture]
public class PublishE2eTests
{
    [Test]
    public async Task Test_Publish_AfterStart_PublishesMessageToSubscribers()
    {
        string topic = "publish-test";
        int totalCount = 3;
        await using PubsubE2eTestSetup test = new();

        await test.AddPeersAsync(totalCount);
        test.Subscribe(topic);

        int i = 0;
        foreach ((_, var peerStore) in test.PeerStores)
        {
            for (int j = 0; j < totalCount; j++)
            {
                if (i != j) peerStore.Discover([.. test.Peers[j].ListenAddresses]);
            }
            i++;
        }

        await test.WaitForFullMeshAsync(topic);

        var receivedMessages = new ConcurrentBag<(int RouterId, byte[] Message)>();

        foreach (var (routerId, router) in test.Routers)
        {
            router.OnMessage += (t, data) =>
            {
                if (t == topic)
                {
                    receivedMessages.Add((routerId, data));
                }
            };
        }

        Random random = new();
        byte[] testMessage = new byte[32];
        random.NextBytes(testMessage);

        // publish from router 0 (router is started in PubsubE2eTestSetup.AddAt)
        test.Routers[0].Publish(topic, testMessage);

        // allow propagation
        await Task.Delay(500);

        Assert.That(receivedMessages.Count, Is.GreaterThan(0), "Publish after StartAsync should deliver messages across the mesh");

        test.PrintState();
    }
}
