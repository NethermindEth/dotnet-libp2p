// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.Pubsub;
using NUnit.Framework;

namespace Libp2p.Protocols.Pubsub.E2eTests;

[TestFixture]
public class PeerScoringE2eTests
{
    [Test]
    public async Task Test_PeerScore_DefaultTopicParams_MessagesPropagate()
    {
        string commonTopic = "test";

        int totalCount = 3;
        await using PubsubE2eTestSetup test = new();

        await test.AddPeersAsync(totalCount);
        test.Subscribe(commonTopic);

        int i = 0;
        foreach ((_, var peerStore) in test.PeerStores)
        {
            for (int j = 0; j < totalCount; j++)
            {
                if (i != j) peerStore.Discover([.. test.Peers[j].ListenAddresses]);
            }
            i++;
        }

        await test.WaitForFullMeshAsync(commonTopic);

        // Track received messages
        var receivedMessages = new System.Collections.Concurrent.ConcurrentBag<(int RouterId, byte[] Message)>();

        foreach (var (routerId, router) in test.Routers)
        {
            router.OnMessage += (topic, data) =>
            {
                if (topic == commonTopic)
                {
                    receivedMessages.Add((routerId, data));
                }
            };
        }

        Random random = new();
        byte[] testMessage = new byte[32];
        random.NextBytes(testMessage);

        test.Routers[0].Publish(commonTopic, testMessage);

        // Wait for message propagation
        await Task.Delay(500);

        // Verify that messages propagated to other peers (scoring doesn't block message delivery by default)
        Assert.That(receivedMessages.Count, Is.GreaterThan(0),
            "Messages should propagate even without explicit topic score params");

        test.PrintState();
    }

    [Test]
    public async Task Test_PeerScore_WithConfiguredTopicParams_MessagesPropagate()
    {
        string commonTopic = "test";

        int totalCount = 3;
        await using PubsubE2eTestSetup test = new();

        // Configure explicit topic parameters with positive weights
        test.DefaultSettings.TopicScoreParams[commonTopic] = new TopicScoreParams
        {
            TopicWeight = 1.0,
            TimeInMeshWeight = 0.01,
            TimeInMeshQuantum = 1000,
            TimeInMeshCap = 3600.0,
            FirstMessageDeliveriesWeight = 1.0,
            FirstMessageDeliveriesDecay = 0.99,
            FirstMessageDeliveriesCap = 2000.0,
        };

        await test.AddPeersAsync(totalCount);
        test.Subscribe(commonTopic);

        int i = 0;
        foreach ((_, var peerStore) in test.PeerStores)
        {
            for (int j = 0; j < totalCount; j++)
            {
                if (i != j) peerStore.Discover([.. test.Peers[j].ListenAddresses]);
            }
            i++;
        }

        await test.WaitForFullMeshAsync(commonTopic);

        // Track received messages
        var receivedMessages = new System.Collections.Concurrent.ConcurrentBag<(int RouterId, byte[] Message)>();

        foreach (var (routerId, router) in test.Routers)
        {
            router.OnMessage += (topic, data) =>
            {
                if (topic == commonTopic)
                {
                    receivedMessages.Add((routerId, data));
                }
            };
        }

        Random random = new();
        for (int j = 0; j < 10; j++)
        {
            byte[] bytes = new byte[32];
            random.NextBytes(bytes);
            test.Routers[0].Publish(commonTopic, bytes);
            await Task.Delay(10);
        }

        // Wait for messages to propagate
        await Task.Delay(500);

        // Verify that messages propagated to peers with configured scoring
        Assert.That(receivedMessages.Count, Is.GreaterThan(0),
            "Messages should propagate with configured topic score params");

        test.PrintState();
    }

    [Test]
    public async Task Test_PeerScore_TimeInMesh_DoesNotBlockMessages()
    {
        string commonTopic = "test";

        int totalCount = 2;
        await using PubsubE2eTestSetup test = new();

        // Configure topic parameters with time-in-mesh weight
        test.DefaultSettings.TopicScoreParams[commonTopic] = new TopicScoreParams
        {
            TopicWeight = 1.0,
            TimeInMeshWeight = 0.1,
            TimeInMeshQuantum = 100, // 100ms
            TimeInMeshCap = 3600.0,
        };

        await test.AddPeersAsync(totalCount);
        test.Subscribe(commonTopic);

        foreach ((_, var peerStore) in test.PeerStores)
        {
            for (int j = 0; j < totalCount; j++)
            {
                peerStore.Discover([.. test.Peers[j].ListenAddresses]);
            }
        }

        await test.WaitForFullMeshAsync(commonTopic);

        // Wait for some time to accumulate time-in-mesh score
        await Task.Delay(1000);

        // Track received messages
        var receivedMessages = new System.Collections.Concurrent.ConcurrentBag<(int RouterId, byte[] Message)>();

        foreach (var (routerId, router) in test.Routers)
        {
            router.OnMessage += (topic, data) =>
            {
                if (topic == commonTopic)
                {
                    receivedMessages.Add((routerId, data));
                }
            };
        }

        Random random = new();
        byte[] testMessage = new byte[32];
        random.NextBytes(testMessage);

        test.Routers[0].Publish(commonTopic, testMessage);

        // Wait for message propagation
        await Task.Delay(500);

        // Verify messages still propagate
        Assert.That(receivedMessages.Count, Is.GreaterThan(0),
            "Messages should propagate with time-in-mesh scoring");

        test.PrintState();
    }

    [Test]
    public async Task Test_PeerScore_WithMultipleTopics()
    {
        string topic1 = "test1";
        string topic2 = "test2";

        int totalCount = 3;
        await using PubsubE2eTestSetup test = new();

        // Configure different parameters for different topics
        test.DefaultSettings.TopicScoreParams[topic1] = new TopicScoreParams
        {
            TopicWeight = 1.0,
            FirstMessageDeliveriesWeight = 1.0,
            FirstMessageDeliveriesDecay = 0.99,
        };

        test.DefaultSettings.TopicScoreParams[topic2] = new TopicScoreParams
        {
            TopicWeight = 0.5,
            FirstMessageDeliveriesWeight = 0.5,
            FirstMessageDeliveriesDecay = 0.99,
        };

        await test.AddPeersAsync(totalCount);
        test.Subscribe(topic1);
        test.Subscribe(topic2);

        foreach ((_, var peerStore) in test.PeerStores)
        {
            for (int j = 0; j < totalCount; j++)
            {
                peerStore.Discover([.. test.Peers[j].ListenAddresses]);
            }
        }

        // Longer timeout: two topics need more time for mesh convergence under load/CI
        await test.WaitForFullMeshAsync(topic1, timeoutMs: 25_000);
        await test.WaitForFullMeshAsync(topic2, timeoutMs: 25_000);

        // Track received messages per topic
        var receivedTopic1 = new System.Collections.Concurrent.ConcurrentBag<int>();
        var receivedTopic2 = new System.Collections.Concurrent.ConcurrentBag<int>();

        foreach (var (routerId, router) in test.Routers)
        {
            router.OnMessage += (topic, data) =>
            {
                if (topic == topic1) receivedTopic1.Add(routerId);
                if (topic == topic2) receivedTopic2.Add(routerId);
            };
        }

        Random random = new();

        // Publish to topic1
        byte[] message1 = new byte[32];
        random.NextBytes(message1);
        test.Routers[0].Publish(topic1, message1);

        // Publish to topic2
        byte[] message2 = new byte[32];
        random.NextBytes(message2);
        test.Routers[1].Publish(topic2, message2);

        // Wait for message propagation
        await Task.Delay(500);

        // Verify messages propagated to both topics
        Assert.That(receivedTopic1.Count, Is.GreaterThan(0), "Messages should propagate on topic1");
        Assert.That(receivedTopic2.Count, Is.GreaterThan(0), "Messages should propagate on topic2");

        test.PrintState();
    }
}
