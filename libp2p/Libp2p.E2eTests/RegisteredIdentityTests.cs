// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Tls;
using NUnit.Framework;
using System.Security.Cryptography;

namespace Libp2p.E2eTests;

public class RegisteredIdentityTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RegisteredSecurityRejectsAnAliasExpectedPeerId(bool tls)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await using ServiceProvider firstServices = Services(tls);
        await using ServiceProvider secondServices = Services(tls);
        await using ILocalPeer first = firstServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer second = secondServices.GetRequiredService<IPeerFactory>().Create(AlternateIdentity(KeyType.Secp256K1, hybrid: true));
        await second.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], deadline.Token);
        byte[] aliasBytes = Multiformats.Hash.Multihash.Sum(Multiformats.Hash.HashType.SHA2_256, second.Identity.PublicKey.ToByteArray());
        PeerId alias = new(aliasBytes);
        Assert.That(alias, Is.Not.EqualTo(second.Identity.PeerId));
        Multiaddress destination = second.ListenAddresses.Single().ToString().Replace($"/p2p/{second.Identity.PeerId}", $"/p2p/{alias}");
        int connections = 0;
        TaskCompletionSource<ISession> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        first.OnConnected += session =>
        {
            Interlocked.Increment(ref connections);
            connected.TrySetResult(session);
        };
        Exception? failure = Assert.CatchAsync(async () =>
            await first.DialAsync(destination, deadline.Token).WaitAsync(deadline.Token));
        // Session shutdown can cancel its pending upgrade before the transport
        // reports PeerConnectionException; neither is the test's deadline.
        Assert.That(failure, Is.TypeOf<TaskCanceledException>().Or.TypeOf<Nethermind.Libp2p.Core.Exceptions.PeerConnectionException>());
        Assert.That(deadline.IsCancellationRequested, Is.False);
        Assert.That(connections, Is.Zero);
        Assert.That(firstServices.GetRequiredService<PeerStore>().TryGetPeerInfo(alias, out _), Is.False);
        ISession canonical = await first.DialAsync([.. second.ListenAddresses], deadline.Token).WaitAsync(deadline.Token);
        Assert.That(await connected.Task.WaitAsync(deadline.Token), Is.SameAs(canonical));
        Assert.That(canonical.RemotePeerId, Is.EqualTo(second.Identity.PeerId));
        Assert.That(connections, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    [Explicit("Requires the host WebRTC runtime, like the WebRTC integration suite.")]
    public async Task RegisteredWebRtcUsesCanonicalIdentity(bool alternateEncoding)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await using ServiceProvider firstServices = new ServiceCollection()
            .AddLibp2p(builder => builder.WithWebRtcDirect().AddProtocol<IncrementNumberTestProtocol>())
            .AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Debug)).BuildServiceProvider();
        await using ServiceProvider secondServices = new ServiceCollection()
            .AddLibp2p(builder => builder.WithWebRtcDirect().AddProtocol<IncrementNumberTestProtocol>())
            .AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Debug)).BuildServiceProvider();
        await using ILocalPeer first = firstServices.GetRequiredService<IPeerFactory>().Create(
            alternateEncoding ? AlternateIdentity(KeyType.Secp256K1, hybrid: false) : new Identity(keyType: KeyType.Secp256K1));
        await using ILocalPeer second = secondServices.GetRequiredService<IPeerFactory>().Create(
            alternateEncoding ? AlternateIdentity(KeyType.Secp256K1, hybrid: true) : new Identity(keyType: KeyType.Secp256K1));
        TaskCompletionSource<ISession> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        second.OnConnected += session => accepted.TrySetResult(session);
        await second.StartListenAsync(["/ip4/127.0.0.1/udp/0/webrtc-direct"], deadline.Token).WaitAsync(deadline.Token);
        Multiaddress destination = $"{second.ListenAddresses.Single()}/p2p/{second.Identity.PeerId}";
        ISession outbound = await first.DialAsync(destination, deadline.Token).WaitAsync(deadline.Token);
        ISession inbound = await accepted.Task.WaitAsync(deadline.Token);
        Assert.That(outbound.RemotePeerId, Is.EqualTo(second.Identity.PeerId));
        Assert.That(inbound.RemotePeerId, Is.EqualTo(first.Identity.PeerId));
        Assert.That(await outbound.DialAsync<IncrementNumberTestProtocol, int, int>(41, deadline.Token).WaitAsync(deadline.Token), Is.EqualTo(42));
        Assert.That(await inbound.DialAsync<IncrementNumberTestProtocol, int, int>(41, deadline.Token).WaitAsync(deadline.Token), Is.EqualTo(42));
    }

    public static IEnumerable<TestCaseData> Cases
    {
        get
        {
            foreach (KeyType type in Enum.GetValues<KeyType>())
                foreach (int protocol in Enumerable.Range(0, 5))
                    foreach (bool tls in new[] { false, true })
                        yield return new TestCaseData(type, protocol, tls).SetName($"RegisteredIdentity_{type}_Pubsub{protocol}_{(tls ? "Tls" : "Noise")}");
        }
    }

    [TestCaseSource(nameof(Cases))]
    public async Task RegisteredProtocolsUseCanonicalIdentitiesAndVerifySignatures(KeyType type, int protocol, bool tls)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        await using ServiceProvider firstServices = Services(tls);
        await using ServiceProvider secondServices = Services(tls);
        Identity firstIdentity = AlternateIdentity(type, hybrid: false);
        Identity secondIdentity = AlternateIdentity(type, hybrid: true);
        await using ILocalPeer first = firstServices.GetRequiredService<IPeerFactory>().Create(firstIdentity);
        await using ILocalPeer second = secondServices.GetRequiredService<IPeerFactory>().Create(secondIdentity);
        await first.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], deadline.Token);
        await second.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], deadline.Token);
        PubsubRouter firstRouter = firstServices.GetRequiredService<PubsubRouter>();
        PubsubRouter secondRouter = secondServices.GetRequiredService<PubsubRouter>();
        await firstRouter.StartAsync(first, deadline.Token);
        await secondRouter.StartAsync(second, deadline.Token);
        const string topic = "registered-signatures";
        TaskCompletionSource<byte[]> receivedAtFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<byte[]> receivedAtSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstCount = 0;
        int secondCount = 0;
        firstRouter.GetTopic(topic).OnMessage += (_, data) =>
        {
            if (Interlocked.Increment(ref firstCount) == 20) receivedAtFirst.TrySetResult(data);
        };
        secondRouter.GetTopic(topic).OnMessage += (_, data) =>
        {
            if (Interlocked.Increment(ref secondCount) == 20) receivedAtSecond.TrySetResult(data);
        };
        TaskCompletionSource<ISession> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        second.OnConnected += session => accepted.TrySetResult(session);
        ISession outbound = await first.DialAsync([.. second.ListenAddresses], deadline.Token).WaitAsync(deadline.Token);
        ISession inbound = await accepted.Task.WaitAsync(deadline.Token);
        Assert.That(outbound.RemotePeerId, Is.EqualTo(secondIdentity.PeerId));
        Assert.That(inbound.RemotePeerId, Is.EqualTo(firstIdentity.PeerId));
        Assert.That(outbound.RemoteAddress.GetPeerId(), Is.EqualTo(secondIdentity.PeerId));
        Assert.That(inbound.RemoteAddress.GetPeerId(), Is.EqualTo(firstIdentity.PeerId));
        Assert.That(firstServices.GetRequiredService<PeerStore>().GetPeerInfo(secondIdentity.PeerId).SignedPeerRecord, Is.Not.Null);
        Assert.That(secondServices.GetRequiredService<PeerStore>().GetPeerInfo(firstIdentity.PeerId).SignedPeerRecord, Is.Not.Null);

        Task stream = protocol switch
        {
            0 => outbound.DialAsync<FloodsubProtocol>(deadline.Token),
            1 => outbound.DialAsync<GossipsubProtocol>(deadline.Token),
            2 => outbound.DialAsync<GossipsubProtocolV11>(deadline.Token),
            3 => outbound.DialAsync<GossipsubProtocolV12>(deadline.Token),
            _ => outbound.DialAsync<GossipsubProtocolV13>(deadline.Token),
        };
        try
        {
            while (!Subscribed(firstRouter, secondIdentity.PeerId, topic, protocol) || !Subscribed(secondRouter, firstIdentity.PeerId, topic, protocol))
                await Task.Delay(10, deadline.Token);
            await firstRouter.Heartbeat();
            await secondRouter.Heartbeat();
            for (int i = 0; i < 20; i++)
            {
                firstRouter.Publish(topic, [1, 2, 3]);
                secondRouter.Publish(topic, [4, 5, 6]);
            }
            Assert.That(await receivedAtFirst.Task.WaitAsync(deadline.Token), Is.EqualTo(new byte[] { 4, 5, 6 }));
            Assert.That(await receivedAtSecond.Task.WaitAsync(deadline.Token), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(firstCount, Is.EqualTo(20));
            Assert.That(secondCount, Is.EqualTo(20));
        }
        finally
        {
            await firstRouter.DisposeAsync();
            await secondRouter.DisposeAsync();
            await stream.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static bool Subscribed(PubsubRouter router, PeerId remote, string topic, int protocol)
    {
        IRoutingStateContainer state = router;
        var peers = protocol == 0 ? state.FloodsubPeers : state.GossipsubPeers;
        return peers.TryGetValue(topic, out HashSet<PeerId>? members) && members.Contains(remote);
    }

    private static ServiceProvider Services(bool tls)
    {
        IServiceCollection services = new ServiceCollection();
        if (tls) services.AddLibp2p<TlsBuilder>(builder => ((ILibp2pPeerFactoryBuilder)builder).WithPubsub());
        else services.AddLibp2p(builder => builder.WithPubsub());
        return services.AddSingleton(new PubsubSettings { AutoConnect = false, HeartbeatInterval = int.MaxValue }).BuildServiceProvider();
    }

    private static Identity AlternateIdentity(KeyType type, bool hybrid)
    {
        Identity identity = new(keyType: type);
        if (type == KeyType.Secp256K1)
        {
            using ECDsa key = ECDsa.Create();
            key.ImportParameters(new ECParameters { Curve = ECCurve.CreateFromFriendlyName("secp256k1"), D = identity.PrivateKey!.Data.ToByteArray() });
            ECPoint pointData = key.ExportParameters(false).Q;
            byte[] point = [0x04, .. pointData.X!, .. pointData.Y!];
            if (hybrid) point[0] = (byte)(0x06 | (point[^1] & 1));
            identity.PublicKey.Data = ByteString.CopyFrom(point);
        }
        identity.PublicKey.MergeFrom(new byte[] { 0x18, 0x01 });
        return identity;
    }

    public sealed class TlsBuilder(IServiceProvider provider) : Libp2pPeerFactoryBuilder(provider)
    {
        protected override ProtocolRef[] Encryption() => [Get<TlsProtocol>()];
    }
}
