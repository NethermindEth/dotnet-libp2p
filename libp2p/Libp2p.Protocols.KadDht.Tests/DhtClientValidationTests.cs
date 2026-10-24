// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Text;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Nethermind.Kademlia;
using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests;

public class DhtClientValidationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task SelectsAndCachesOnlyValidatedCandidate(bool fullCache)
    {
        var nodes = Enumerable.Range(1, 3).Select(i => new Identity(Enumerable.Repeat((byte)i, 32).ToArray()).PeerId.ToDhtNode()).ToArray();
        var table = Substitute.For<IRoutingTable<DhtNode, ValueHash256>>();
        table.GetKNearestNeighbour(Arg.Any<ValueHash256>(), true).Returns(nodes);
        var state = new SharedDhtState(table);
        var values = new InMemoryValueStore(1);
        if (fullCache) await values.PutValueAsync("other"u8.ToArray(), Stored("kept"));
        var sender = Substitute.For<IDhtMessageSender>();
        for (int i = 0; i < nodes.Length; i++)
            sender.GetValueAsync(nodes[i], Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
                .Returns(new GetValueResult { Value = Encoding.UTF8.GetBytes(new[] { "bad", "old", "new" }[i]), Timestamp = i + 1 });
        var client = new DhtClient(state, sender, values, new SelectingValidator(), new KadDhtOptions());
        Assert.That(await client.GetValueAsync("key"), Is.EqualTo((true, "new")));
        var cached = await values.GetValueAsync("key"u8.ToArray());
        Assert.That(cached?.Value, fullCache ? Is.Null : Is.EqualTo("new"u8.ToArray()));
        Assert.That(values.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task InvalidRemoteAndCacheValuesNeverEscape()
    {
        var node = new Identity().PeerId.ToDhtNode();
        var table = Substitute.For<IRoutingTable<DhtNode, ValueHash256>>();
        table.GetKNearestNeighbour(Arg.Any<ValueHash256>(), true).Returns([node]);
        var values = new InMemoryValueStore();
        await values.PutValueAsync("key"u8.ToArray(), Stored("bad"));
        var sender = Substitute.For<IDhtMessageSender>();
        sender.GetValueAsync(node, Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(new GetValueResult { Value = "bad"u8.ToArray() });
        var client = new DhtClient(new SharedDhtState(table), sender, values, new SelectingValidator(), new KadDhtOptions());
        Assert.That(await client.GetValueAsync("key"), Is.EqualTo((false, (string?)null)));
        Assert.That((await values.GetValueAsync("key"u8.ToArray()))!.Value, Is.EqualTo("bad"u8.ToArray()));
        Assert.That(await client.PutValueAsync("another", "bad"), Is.Zero);
        await sender.DidNotReceive().PutValueAsync(Arg.Any<DhtNode>(), Arg.Any<byte[]>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RejectedLocalPutIsNotCountedAndSizeIsEnforced()
    {
        var sender = Substitute.For<IDhtMessageSender>();
        var client = new DhtClient(new SharedDhtState(), sender, new InMemoryValueStore(0),
            DefaultRecordValidator.Instance, new KadDhtOptions { MaxValueSize = 4 });
        Assert.That(await client.PutValueAsync("key", "okay"), Is.Zero);
        Assert.ThrowsAsync<ArgumentException>(async () => await client.PutValueAsync("key", "oversized"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CallerCancellationFromNetworkPropagates(bool put)
    {
        var node = new Identity().PeerId.ToDhtNode();
        var table = Substitute.For<IRoutingTable<DhtNode, ValueHash256>>();
        table.GetKNearestNeighbour(Arg.Any<ValueHash256>(), true).Returns([node]);
        using var stop = new CancellationTokenSource();
        var sender = Substitute.For<IDhtMessageSender>();
        sender.PutValueAsync(node, Arg.Any<byte[]>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            stop.Cancel();
            return Task.FromCanceled<bool>(stop.Token);
        });
        sender.GetValueAsync(node, Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            stop.Cancel();
            return Task.FromCanceled<GetValueResult>(stop.Token);
        });
        var client = new DhtClient(new SharedDhtState(table), sender, new InMemoryValueStore(),
            DefaultRecordValidator.Instance, new KadDhtOptions());
        Assert.That(async () =>
        {
            if (put) await client.PutValueAsync("key", "value", stop.Token);
            else await client.GetValueAsync("key", stop.Token);
        }, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task DefaultProductionValidatorRejectsUnsignedIpns()
    {
        var values = new InMemoryValueStore();
        var client = new DhtClient(new SharedDhtState(), Substitute.For<IDhtMessageSender>(), values,
            CompositeRecordValidator.CreateDefault(), new KadDhtOptions());
        Assert.That(await client.PutValueAsync("/ipns/unsigned", "unsigned"), Is.Zero);
        Assert.That(values.Count, Is.Zero);
    }

    [Test]
    public async Task DisposalCancelsAndDrainsPendingLookup()
    {
        var node = new Identity().PeerId.ToDhtNode();
        var table = Substitute.For<IRoutingTable<DhtNode, ValueHash256>>();
        table.GetKNearestNeighbour(Arg.Any<ValueHash256>(), true).Returns([node]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = Substitute.For<IDhtMessageSender>();
        sender.GetValueAsync(node, Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return new GetValueResult();
        });
        using var client = new DhtClient(new SharedDhtState(table), sender, new InMemoryValueStore(),
            DefaultRecordValidator.Instance, new KadDhtOptions());
        Task lookup = client.GetValueAsync("key");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Run(client.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(async () => await lookup, Throws.InstanceOf<OperationCanceledException>());
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await client.GetValueAsync("key"));
    }

    private static StoredValue Stored(string value) => new() { Value = Encoding.UTF8.GetBytes(value), Ttl = TimeSpan.FromHours(1) };

    private sealed class SelectingValidator : IRecordValidator
    {
        public bool Validate(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value) => !value.SequenceEqual("bad"u8);
        public int Select(ReadOnlySpan<byte> key, IReadOnlyList<byte[]> values) =>
            values.Select((value, index) => (value, index)).First(pair => pair.value.AsSpan().SequenceEqual("new"u8)).index;
    }
}
