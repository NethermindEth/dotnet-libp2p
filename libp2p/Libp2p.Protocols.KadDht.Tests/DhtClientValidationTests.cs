// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Text;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Kademlia;
using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests;

public class DhtClientValidationTests
{
    [Test]
    [Obsolete("Exercises a supplied state with an already exposed legacy facade.")]
    public async Task ConflictingRegisteredRuntimeDoesNotMutateSuppliedState()
    {
        var state = new SharedDhtState();
        var facade = state.ValueStore;
        var peer = Substitute.For<ILocalPeer>();
        peer.Identity.Returns(new Identity());
        peer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        await using var services = new ServiceCollection().AddSingleton(peer).AddSingleton(state)
            .AddSingleton<IValueStore>(new InMemoryValueStore()).AddKadDht().BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<DhtClient>());
        Assert.That(state.LocalPeerKey, Is.Null, "Reject the binding before constructing a protocol or changing shared routing/identity state.");
        Assert.That(state.AddNodeCallback, Is.Null);
        Assert.That(state.ValueStore, Is.SameAs(facade));
    }

    [TestCase(false)]
    [TestCase(true)]
    [Obsolete("Exercises the legacy client constructor and SharedDhtState.ValueStore facade.")]
    public async Task LegacyAndModernClientsKeepTheSameStoreAndTtl(bool legacyFirst)
    {
        var state = new SharedDhtState();
        var values = new InMemoryValueStore();
        var options = new KadDhtOptions { RecordTtl = TimeSpan.FromMinutes(15) };
        DhtClient? modern = legacyFirst ? null : new DhtClient(state, Substitute.For<IDhtMessageSender>(), values,
            DefaultRecordValidator.Instance, options);
        await using var sender = new global::Libp2p.Protocols.KadDht.Network.LibP2pKademliaMessageSender<PublicKey, DhtNode>(Substitute.For<ILocalPeer>());
        await using var legacy = new DhtClient(state, sender);
        var facade = state.ValueStore;
        modern ??= new DhtClient(state, Substitute.For<IDhtMessageSender>(), facade.Store,
            DefaultRecordValidator.Instance, new KadDhtOptions { RecordTtl = facade.DefaultTtl });
        await using (modern)
        {
            Assert.That(await legacy.PutValueAsync("legacy", "value"), Is.EqualTo(1));
            Assert.That((await facade.Store.GetValueAsync("legacy"u8.ToArray()))!.Ttl, Is.EqualTo(facade.DefaultTtl));
            Assert.That(await modern.GetValueAsync("legacy"), Is.EqualTo((true, "value")));
            Assert.Throws<InvalidOperationException>(() => new DhtClient(state, Substitute.For<IDhtMessageSender>(),
                new InMemoryValueStore(), DefaultRecordValidator.Instance, new KadDhtOptions()));
            Assert.That(state.ValueStore, Is.SameAs(facade));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [Obsolete("Exercises the public SharedDhtState.ValueStore compatibility facade.")]
    public async Task SharedStateRejectsConflictingClientStoreOrTtl(bool differentTtl)
    {
        var state = new SharedDhtState();
        var values = new InMemoryValueStore();
        var options = new KadDhtOptions { RecordTtl = TimeSpan.FromMinutes(15) };
        await using var first = new DhtClient(state, Substitute.For<IDhtMessageSender>(), values,
            DefaultRecordValidator.Instance, options);
        var facade = state.ValueStore;
        await using var same = new DhtClient(state, Substitute.For<IDhtMessageSender>(), values,
            DefaultRecordValidator.Instance, new KadDhtOptions { RecordTtl = options.RecordTtl });
        Assert.That(state.ValueStore, Is.SameAs(facade));

        Assert.Throws<InvalidOperationException>(() => new DhtClient(state, Substitute.For<IDhtMessageSender>(),
            differentTtl ? values : new InMemoryValueStore(), DefaultRecordValidator.Instance,
            new KadDhtOptions { RecordTtl = differentTtl ? TimeSpan.FromMinutes(30) : options.RecordTtl }));

        Assert.That(state.ValueStore, Is.SameAs(facade));
        Assert.That(await same.PutValueAsync("key", "value"), Is.EqualTo(1));
        Assert.That(facade.Get("key"u8.ToArray())?.Value, Is.EqualTo("value"u8.ToArray()));
        Assert.That(facade.Put("legacy"u8.ToArray(), "value"u8.ToArray()), Is.True);
        Assert.That((await values.GetValueAsync("legacy"u8.ToArray()))!.Ttl, Is.EqualTo(options.RecordTtl));
    }

    [Test]
    [Obsolete("Exercises the public SharedDhtState.ValueStore compatibility facade.")]
    public async Task ConcurrentClientsBindSharedStateToExactlyOneStore()
    {
        var state = new SharedDhtState();
        var stores = new[] { new InMemoryValueStore(), new InMemoryValueStore() };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = stores.Select(values => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                return (Client: new DhtClient(state, Substitute.For<IDhtMessageSender>(), values,
                    DefaultRecordValidator.Instance, new KadDhtOptions()), Error: (Exception?)null);
            }
            catch (Exception error) { return (Client: (DhtClient?)null, Error: error); }
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);
        try
        {
            Assert.That(results.Count(result => result.Client is not null), Is.EqualTo(1));
            Assert.That(results.Single(result => result.Error is not null).Error, Is.TypeOf<InvalidOperationException>());
            int winner = Array.FindIndex(results, result => result.Client is not null);
            var facade = state.ValueStore;
            Assert.That(await results[winner].Client!.PutValueAsync("key", "value"), Is.EqualTo(1));
            Assert.That(facade.Get("key"u8.ToArray())?.Value, Is.EqualTo("value"u8.ToArray()));
            Assert.That(stores[winner].Count, Is.EqualTo(1));
            Assert.That(stores[1 - winner].Count, Is.Zero);
        }
        finally
        {
            foreach (var result in results)
                if (result.Client is not null) await result.Client.DisposeAsync();
        }
    }

    [Test]
    [Obsolete("Exercises the public SharedDhtState.ValueStore compatibility facade.")]
    public void ExposedLegacyFacadeCannotBeReboundByAClient()
    {
        var state = new SharedDhtState();
        var facade = state.ValueStore;
        Assert.That(facade.Put("key"u8.ToArray(), "kept"u8.ToArray()), Is.True);
        Assert.Throws<InvalidOperationException>(() => new DhtClient(state, Substitute.For<IDhtMessageSender>(),
            new InMemoryValueStore(), DefaultRecordValidator.Instance, new KadDhtOptions()));
        Assert.That(state.ValueStore, Is.SameAs(facade));
        Assert.That(facade.Get("key"u8.ToArray())?.Value, Is.EqualTo("kept"u8.ToArray()));
    }

    [Test]
    [Obsolete("Exercises the public SharedDhtState.ValueStore compatibility facade.")]
    public async Task PublicStateLegacyFacadeUsesConfiguredClientStoreAndTtl()
    {
        var state = new SharedDhtState();
        var values = new InMemoryValueStore(1);
        var options = new KadDhtOptions { RecordTtl = TimeSpan.FromMinutes(15) };
        await using var client = new DhtClient(state, Substitute.For<IDhtMessageSender>(), values,
            DefaultRecordValidator.Instance, options);

        Assert.That(await client.PutValueAsync("one", "client"), Is.EqualTo(1));
        Assert.That(state.ValueStore.Get("one"u8.ToArray())?.Value, Is.EqualTo("client"u8.ToArray()));
        Assert.That(state.ValueStore.Put("two"u8.ToArray(), "legacy"u8.ToArray()), Is.False);
        Assert.That(await client.GetValueAsync("two"), Is.EqualTo((false, (string?)null)));

        Assert.That(state.ValueStore.Remove("one"u8.ToArray()), Is.True);
        Assert.That(await values.GetValueAsync("one"u8.ToArray()), Is.Null);
        Assert.That(state.ValueStore.Put("two"u8.ToArray(), "legacy"u8.ToArray()), Is.True);
        Assert.That(await client.GetValueAsync("two"), Is.EqualTo((true, "legacy")));
        Assert.That((await values.GetValueAsync("two"u8.ToArray()))!.Ttl, Is.EqualTo(options.RecordTtl));
        Assert.That(values.Count, Is.EqualTo(1));
    }

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
