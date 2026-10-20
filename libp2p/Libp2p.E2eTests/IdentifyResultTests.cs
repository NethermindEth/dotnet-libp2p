// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Identify;
using Nethermind.Libp2p.Protocols.Identify.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class IdentifyResultTests
{
    [TestCase(false, 42UL)]
    [TestCase(true, 0UL)]
    [TestCase(true, 42UL)]
    [TestCase(true, ulong.MaxValue)]
    public async Task DialReturnsReceivedMetadataAndOnlyVerifiedRecord(bool validRecord, ulong sequence)
    {
        Identity remote = TestPeers.Identity(2);
        PeerStore store = new();
        ByteString address = ByteString.CopyFrom(TestPeers.Multiaddr(2).ToBytes());
        Identify message = new()
        {
            PublicKey = remote.PublicKey.ToByteString(),
            AgentVersion = "client/1.2",
            ProtocolVersion = "app/3",
            ObservedAddr = address,
            ListenAddrs = { address },
            Protocols = { "/one/1", "/two/2" },
            SignedPeerRecord = validRecord ? SigningHelper.CreateSignedEnvelope(remote, [], sequence) : ByteString.Empty
        };

        IdentifyResult result = await Dial(remote, store, message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PeerId, Is.EqualTo(remote.PeerId));
            Assert.That(result.AgentVersion, Is.EqualTo("client/1.2"));
            Assert.That(result.ProtocolVersion, Is.EqualTo("app/3"));
            Assert.That(result.Protocols, Is.EqualTo(new[] { "/one/1", "/two/2" }));
            Assert.That(result.ListenAddresses, Is.EqualTo(new[] { address }));
            Assert.That(result.ObservedAddress, Is.EqualTo(address));
            Assert.That(result.SignedPeerRecord, Is.EqualTo(validRecord ? message.SignedPeerRecord : null));
            Assert.That(result.PeerRecordSequence, Is.EqualTo(validRecord ? (ulong?)sequence : null));
            Assert.That(store.GetPeerInfo(remote.PeerId).AgentVersion, Is.EqualTo("client/1.2"));
        }
        store.GetPeerInfo(remote.PeerId).SupportedProtocols![0] = "/changed";
        Assert.That(result.Protocols[0], Is.EqualTo("/one/1"), "The returned metadata is a snapshot.");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Protocols)[0] = "/changed");
    }

    [Test]
    public async Task PartialPushPreservesAgentVersionAndExplicitUpdateReplacesIt()
    {
        Identity remote = TestPeers.Identity(2);
        PeerStore store = new();
        await Dial(remote, store, new Identify { PublicKey = remote.PublicKey.ToByteString(), AgentVersion = "client/1" });
        await Dial(remote, store, new Identify(), push: true);
        Assert.That(store.GetPeerInfo(remote.PeerId).AgentVersion, Is.EqualTo("client/1"));
        await Dial(remote, store, new Identify { AgentVersion = "client/2" }, push: true);
        Assert.That(store.GetPeerInfo(remote.PeerId).AgentVersion, Is.EqualTo("client/2"));
        await Dial(remote, store, new Identify { AgentVersion = "" }, push: true);
        Assert.That(store.GetPeerInfo(remote.PeerId).AgentVersion, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RejectedIdentityDoesNotPublishAgentVersion(bool wrongPublicKey)
    {
        Identity remote = TestPeers.Identity(2);
        PeerStore store = new();
        store.GetPeerInfo(remote.PeerId).AgentVersion = "prior/1";
        Identify message = new()
        {
            PublicKey = (wrongPublicKey ? TestPeers.Identity(3) : remote).PublicKey.ToByteString(),
            AgentVersion = "rejected/2"
        };

        Assert.ThrowsAsync<PeerConnectionException>(async () =>
            await Dial(remote, store, message, strict: true));

        Assert.That(store.GetPeerInfo(remote.PeerId).AgentVersion, Is.EqualTo("prior/1"));
    }

    [Test]
    public async Task ResultReportsReceivedRecordWithoutReplacingNewerCachedRecord()
    {
        Identity remote = TestPeers.Identity(2);
        PeerStore store = new();
        ByteString newer = SigningHelper.CreateSignedEnvelope(remote, [], 43);
        store.GetPeerInfo(remote.PeerId).SignedPeerRecord = newer;
        store.GetPeerInfo(remote.PeerId).Seq = 43;
        Identify message = new()
        {
            PublicKey = remote.PublicKey.ToByteString(),
            SignedPeerRecord = SigningHelper.CreateSignedEnvelope(remote, [], 42)
        };

        IdentifyResult result = await Dial(remote, store, message);

        Assert.That(result.PeerRecordSequence, Is.EqualTo(42));
        Assert.That(result.SignedPeerRecord, Is.EqualTo(message.SignedPeerRecord));
        Assert.That(store.GetPeerInfo(remote.PeerId).SignedPeerRecord, Is.EqualTo(newer));
        Assert.That(store.GetPeerInfo(remote.PeerId).Seq, Is.EqualTo(43));
    }

    [Test]
    public async Task SessionDialReturnsTypedIdentifyResult()
    {
        await using IdentifySetup setup = new();
        await setup.AddPeersAsync(2);
        ISession session = await setup.Peers[0].DialAsync([.. setup.Peers[1].ListenAddresses]);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await session.DialAsync<IdentifyProtocol, IdentifyResult>(canceled.Token));

        IdentifyResult result = await session.DialAsync<IdentifyProtocol, IdentifyResult>();

        Assert.That(result.PeerId, Is.EqualTo(setup.Peers[1].Identity.PeerId));
        Assert.That(result.AgentVersion, Is.EqualTo("integration/1"));
        Assert.That(setup.PeerStores[0].GetPeerInfo(result.PeerId).AgentVersion, Is.EqualTo("integration/1"));
        await session.DialAsync<IdentifyProtocol>();
    }

    private static async Task<IdentifyResult> Dial(Identity remote, PeerStore store, Identify message, bool push = false, bool strict = false)
    {
        Channel channel = new();
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2), RemotePublicKey = remote.PublicKey });
        Task writer = channel.Reverse.WriteSizeAndProtobufAsync(message).AsTask();
        try
        {
            if (push)
            {
                await new IdentifyPushProtocol(new ProtocolStackSettings(), peerStore: store).ListenAsync(channel, context);
                return new IdentifyResult { PeerId = remote.PeerId };
            }
            IdentifyProtocolSettings settings = new()
            {
                PeerRecordsVerificationPolicy = strict ? PeerRecordsVerificationPolicy.RequireCorrect : PeerRecordsVerificationPolicy.DoesNotRequire
            };
            return await new IdentifyProtocol(new ProtocolStackSettings(), settings, store).DialAsync(channel, context);
        }
        finally
        {
            await channel.CloseAsync();
            await writer;
        }
    }

    private sealed class IdentifySetup : E2eTestSetup
    {
        protected override Multiaddress[]? GetListenAddresses(int index) => ["/ip4/127.0.0.1/tcp/0"];
        protected override IServiceCollection ConfigureServices(IServiceCollection services)
            => base.ConfigureServices(services).AddSingleton(new IdentifyProtocolSettings { AgentVersion = "integration/1" });
    }
}
