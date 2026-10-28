// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Libp2p.Protocols.KadDht.Tests.Integration.WirePeerSafetyTests;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

public class ReviewFeedbackTests
{
    [TestCase(Message.Types.MessageType.FindNode, null)]
    [TestCase(Message.Types.MessageType.GetValue, null)]
    [TestCase(Message.Types.MessageType.GetProviders, null)]
    [TestCase(Message.Types.MessageType.FindNode, " \t\r\n")]
    [TestCase(Message.Types.MessageType.GetValue, " \t\r\n")]
    [TestCase(Message.Types.MessageType.GetProviders, " \t\r\n")]
    public async Task RepliesSkipBlankCallbackAddressesWithoutChargingBytes(Message.Types.MessageType type, string? skipped)
    {
        const string kept = "/ip6/::1/tcp/4001";
        int bytes = 2 * Id(1).Bytes.Length + 2 * kept.Length;
        var values = Substitute.For<IValueStore>();
        values.GetValueAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(new StoredValue { Value = [10], Timestamp = 1 });
        using var harness = new KadWireHarness("direct", new KadDhtOptions
        {
            MaxAddressesPerPeer = 2,
            MaxPeerRecordBytes = bytes,
            MaxPeerResponseBytes = bytes
        }, values: values, nearest: _ => [Id(1).ToDhtNode([skipped!, kept, "/ip4/2.2.2.2"])]);

        var response = await harness.SendAsync(new Message { Type = type, Key = ByteString.CopyFrom(new byte[] { 1 }) });

        Assert.That(response.CloserPeers.Select(peer => new PeerId(peer.Id.ToByteArray())), Is.EqualTo(new[] { Id(1) }));
        Assert.That(response.CloserPeers.Single().Addrs.Select(address => Multiaddress.Decode(address.ToByteArray()).ToString()),
            Is.EqualTo(new[] { kept }), "Skipped entries consume slots, but no payload bytes.");
        if (type == Message.Types.MessageType.GetValue)
            Assert.That(response.Record?.Value, Is.EqualTo(ByteString.CopyFrom(new byte[] { 10 })));
    }

    [TestCase("services", null)]
    [TestCase("fallback", null)]
    [TestCase("integration", null)]
    [TestCase("direct", null)]
    [TestCase("services", " \t\r\n")]
    [TestCase("fallback", " \t\r\n")]
    [TestCase("integration", " \t\r\n")]
    [TestCase("direct", " \t\r\n")]
    public async Task RepliesSkipBlankCustomProviderAddressesWithoutChargingBytes(string path, string? skipped)
    {
        const string kept = "/ip4/1.1.1.1/tcp/4001";
        var store = Substitute.For<IProviderStore>();
        store.GetProvidersAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new ProviderRecord { PeerId = Id(1), Multiaddrs = [skipped!, kept, "/ip4/2.2.2.2"] } });
        int bytes = 2 * Id(1).Bytes.Length + 2 * kept.Length;
        using var harness = new KadWireHarness(path, new KadDhtOptions
        {
            MaxAddressesPerProvider = 2,
            MaxPeerRecordBytes = bytes,
            MaxPeerResponseBytes = bytes
        }, providers: store);

        var response = await harness.SendAsync(MessageHelper.CreateGetProvidersRequest([1]));

        Assert.That(response.ProviderPeers.Select(peer => new PeerId(peer.Id.ToByteArray())), Is.EqualTo(new[] { Id(1) }));
        Assert.That(response.ProviderPeers.Single().Addrs.Select(address => Multiaddress.Decode(address.ToByteArray()).ToString()),
            Is.EqualTo(new[] { kept }));
    }

    [TestCase("services")]
    [TestCase("fallback")]
    [TestCase("integration")]
    [TestCase("direct")]
    public async Task AnnouncementsDropOtherDestinationsBeforeCustomStorage(string path)
    {
        var store = Substitute.For<IProviderStore>();
        using var harness = new KadWireHarness(path, new KadDhtOptions { MaxAddressesPerProvider = 3 }, providers: store);
        harness.Context.State.RemoteAddress = Multiaddress.Decode($"/p2p/{Id(1)}");
        var request = new Message { Type = Message.Types.MessageType.AddProvider, Key = ByteString.CopyFrom(new byte[] { 1 }) };
        request.ProviderPeers.Add(WirePeer(1,
            $"/ip4/1.1.1.1/tcp/4001/p2p/{Id(2)}",
            $"/ip4/1.1.1.1/tcp/4001/p2p/{Id(3)}/p2p-circuit/p2p/{Id(2)}",
            "/ip6/::1/tcp/4001",
            "/ip4/2.2.2.2/tcp/4001"));

        await harness.SendAsync(request);

        await store.Received(1).AddProviderAsync(Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Is<ProviderRecord>(record => record.PeerId.Equals(Id(1)) &&
                record.Multiaddrs.SequenceEqual(new[] { "/ip6/::1/tcp/4001" })), Arg.Any<CancellationToken>());
    }

    [TestCase(Message.Types.MessageType.FindNode)]
    [TestCase(Message.Types.MessageType.GetValue)]
    [TestCase(Message.Types.MessageType.GetProviders)]
    public async Task RepliesDeduplicateEachListWithoutReplacingPrefixSlots(Message.Types.MessageType type)
    {
        var candidates = new[] { Id(1), Id(1), Id(2), Id(3) };
        var store = Substitute.For<IProviderStore>();
        store.GetProvidersAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(candidates.Select(id => new ProviderRecord { PeerId = id }).ToArray());
        using var harness = new KadWireHarness("direct", new KadDhtOptions { KSize = 3, MaxProvidersPerKey = 3 },
            providers: store, nearest: _ => candidates.Select(id => id.ToDhtNode()));

        var response = await harness.SendAsync(new Message { Type = type, Key = ByteString.CopyFrom(new byte[] { 1 }) });

        Assert.That(response.CloserPeers.Select(peer => new PeerId(peer.Id.ToByteArray())), Is.EqualTo(new[] { Id(1), Id(2) }));
        Assert.That(response.ProviderPeers.Select(peer => new PeerId(peer.Id.ToByteArray())),
            Is.EqualTo(type == Message.Types.MessageType.GetProviders ? new[] { Id(1), Id(2) } : []));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FindNodeExcludesRequesterWithinBoundedCandidatePrefix(bool allRequester)
    {
        int inspected = 0;
        IEnumerable<DhtNode> Nearest()
        {
            for (int i = 0; i < 4; i++)
            {
                inspected++;
                Assert.That(inspected, Is.LessThanOrEqualTo(3), "Requester entries still consume candidate slots.");
                yield return Id(allRequester ? 1 : i + 1).ToDhtNode();
            }
        }
        using var harness = new KadWireHarness("direct", new KadDhtOptions { KSize = 3 }, nearest: _ => Nearest());
        harness.Context.State.RemoteAddress = Multiaddress.Decode($"/p2p/{Id(1)}");

        var response = await harness.SendAsync(MessageHelper.CreateFindNodeRequest([1]));

        Assert.That(inspected, Is.EqualTo(3));
        Assert.That(response.CloserPeers.Select(peer => new PeerId(peer.Id.ToByteArray())),
            Is.EqualTo(allRequester ? [] : new[] { Id(2), Id(3) }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SenderMismatchWarningNamesBothValidatedPeersOnlyWhenEnabled(bool warningEnabled)
    {
        var logger = new WarningLogger(warningEnabled);
        var factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        var store = Substitute.For<IProviderStore>();
        using var harness = new KadWireHarness("fallback", new KadDhtOptions(), providers: store, loggerFactory: factory);
        harness.Context.State.RemoteAddress = Multiaddress.Decode($"/p2p/{Id(2)}");
        var request = new Message { Type = Message.Types.MessageType.AddProvider, Key = ByteString.CopyFrom(new byte[] { 1 }) };
        request.ProviderPeers.Add(WirePeer(1));
        await harness.SendAsync(request);
        request.ProviderPeers[0].Id = ByteString.CopyFrom(new byte[] { 255, 255, 255 });
        await harness.SendAsync(request);

        await store.DidNotReceive().AddProviderAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ProviderRecord>(), Arg.Any<CancellationToken>());
        Assert.That(logger.Warnings, Has.Count.EqualTo(warningEnabled ? 1 : 0));
        if (warningEnabled)
        {
            Assert.That(logger.Warnings.Single(), Does.Contain(Id(1).ToString()).And.Contain(Id(2).ToString()));
        }
    }

    private sealed class WarningLogger(bool enabled) : ILogger
    {
        internal List<string> Warnings { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => enabled && logLevel == LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
