// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Reflection;
using Nethermind.Libp2p.Core.Discovery;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(30_000)]
public class TopicDispatchTests
{
    [Test]
    public void ConcurrentGetTopicLeavesNoDiscardedTopicSubscribedToTheRouter()
    {
        const int Threads = 8;
        const int Topics = 500;
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { AutoConnect = false });
        using Barrier barrier = new(Threads);
        Thread[] threads = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
        {
            for (int i = 0; i < Topics; i++)
            {
                barrier.SignalAndWait();
                router.GetTopic($"topic-{i}", subscribe: false);
            }
        })).ToArray();
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();

        FieldInfo onMessage = typeof(PubsubRouter).GetField(nameof(PubsubRouter.OnMessage), BindingFlags.Instance | BindingFlags.NonPublic)!;
        Delegate[] handlers = ((Delegate?)onMessage.GetValue(router))?.GetInvocationList() ?? [];
        Topic[] discarded = handlers.Select(handler => handler.Target).OfType<Topic>()
            .Where(topic => !ReferenceEquals(topic, router.GetTopic(topic.Name, subscribe: false))).ToArray();

        Assert.That(discarded, Is.Empty, "A topic instance the router does not keep must not stay subscribed to every message.");
    }
}
