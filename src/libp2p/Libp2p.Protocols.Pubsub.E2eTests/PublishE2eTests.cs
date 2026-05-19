// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Discovery;
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
        int totalCount = 2;
        await using PubsubE2eTestSetup test = new();

        await test.AddPeersAsync(totalCount);
        test.Subscribe(topic);

        // Unidirectional discovery: only peer 1 discovers peer 0 (avoids bidirectional dial race)
        foreach ((_, var peerStore) in test.PeerStores.Skip(1))
        {
            peerStore.Discover([.. test.Peers[0].ListenAddresses]);
        }

        await test.WaitForFullMeshAsync(topic, 30_000);

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

        // Wait for at least 1 message to arrive (event-driven, no fixed delay)
        await PubsubE2eTestSetup.WaitForMessagesAsync(receivedMessages, 1);

        Assert.That(receivedMessages.Count, Is.GreaterThan(0), "Publish after StartAsync should deliver messages across the mesh");

        test.PrintState();
    }
}
