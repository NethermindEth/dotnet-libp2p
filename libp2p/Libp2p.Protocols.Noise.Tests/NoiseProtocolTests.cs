// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Noise.Dto;
using Noise;
using Org.BouncyCastle.Crypto.EC;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class NoiseProtocolTests
{
    [Test]
    public void Test_RemoteIdentityRejectsUnexpectedDialedPeer()
    {
        IConnectionContext context = Substitute.For<IConnectionContext>();
        context.State.Returns(new State { RemoteAddress = $"/ip4/127.0.0.1/tcp/0/p2p/{TestPeers.PeerId(3)}" });

        MethodInfo setRemoteIdentity = typeof(NoiseProtocol).GetMethod("SetRemoteIdentity", BindingFlags.Static | BindingFlags.NonPublic)!;

        TargetInvocationException? exception = Assert.Throws<TargetInvocationException>(() =>
            setRemoteIdentity.Invoke(null, [context, TestPeers.Identity(2).PublicKey]));

        Assert.That(exception!.InnerException, Is.TypeOf<Libp2pException>());
    }

    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        // Arrange
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");

        IProtocol? proto2 = Substitute.For<IProtocol>();
        proto2.Id.Returns("proto2");

        // Dialer
        MultiplexerSettings dialerSettings = new();
        dialerSettings.Add(proto2);
        dialerSettings.Add(proto1);

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
        dialerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        dialerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}" });


        TestChannel dialerUpChannel = new();
        dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        NoiseProtocol dialer = new(dialerSettings);

        // Listener
        MultiplexerSettings listenerSettings = new();
        listenerSettings.Add(proto1);

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));
        listenerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(2)]);
        listenerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}" });

        TestChannel listenerUpChannel = new();
        listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        NoiseProtocol listener = new(listenerSettings);

        // Act
        Task listenTask = listener.ListenAsync(downChannel, listenerContext);
        Task dialTask = dialer.DialAsync(downChannelFromProtocolPov, dialerContext);

        int sent = 42;
        ValueTask<IOResult> writeTask = dialerUpChannel.Reverse().WriteVarintAsync(sent);
        int received = await listenerUpChannel.Reverse().ReadVarintAsync();
        await writeTask;

        await dialerUpChannel.CloseAsync();
        await listenerUpChannel.CloseAsync();
        await downChannel.CloseAsync();

        await dialTask;
        await listenTask;

        Assert.That(received, Is.EqualTo(sent));
    }

    [Test]
    public void Test_DialerRejects_WhenResponderSignatureIsTampered()
    {
        // Arrange: malicious listener that sends a valid noise msg1 but with a corrupted signature
        var maliciousListener = new MaliciousResponderProtocol(
            MaliciousResponderProtocol.CorruptionMode.TamperedSignature,
            new MultiplexerSettings());

        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
        dialerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        dialerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}" });

        TestChannel dialerUpChannel = new();
        dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        NoiseProtocol dialer = new();

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));
        listenerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(2)]);
        listenerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}" });

        TestChannel listenerUpChannel = new();
        listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        // Act
        Task listenTask = maliciousListener.ListenAsync(downChannel, listenerContext);
        Task dialTask = dialer.DialAsync(downChannelFromProtocolPov, dialerContext);

        // Assert
        Assert.ThrowsAsync<Libp2pException>(async () => await dialTask,
            "Dialer should reject connection when responder signature is tampered");

        listenTask.Wait(5000);
    }

    [Test]
    public void Test_DialerRejects_WhenResponderSignatureIsEmpty()
    {
        var maliciousListener = new MaliciousResponderProtocol(
            MaliciousResponderProtocol.CorruptionMode.EmptySignature,
            new MultiplexerSettings());

        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
        dialerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        dialerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}" });

        TestChannel dialerUpChannel = new();
        dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        NoiseProtocol dialer = new();

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));
        listenerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(2)]);
        listenerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}" });

        TestChannel listenerUpChannel = new();
        listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        Task listenTask = maliciousListener.ListenAsync(downChannel, listenerContext);
        Task dialTask = dialer.DialAsync(downChannelFromProtocolPov, dialerContext);

        Assert.ThrowsAsync<Libp2pException>(async () => await dialTask,
            "Dialer should reject connection when responder signature is empty");

        listenTask.Wait(5000);
    }

    /// <remarks>
    /// The signature is checked before the peer ID, so an unusable key reaches
    /// <see cref="Identity.VerifySignature"/>, which used to throw its own
    /// exception out of the handshake.
    /// </remarks>
    [Test]
    public void Test_DialerRejects_WhenResponderIdentityKeyIsUnusable()
    {
        var maliciousListener = new MaliciousResponderProtocol(
            MaliciousResponderProtocol.CorruptionMode.UnusableIdentityKey,
            new MultiplexerSettings());

        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
        dialerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        dialerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}" });

        TestChannel dialerUpChannel = new();
        dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        NoiseProtocol dialer = new();

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));
        listenerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(2)]);
        listenerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}" });

        TestChannel listenerUpChannel = new();
        listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        Task listenTask = maliciousListener.ListenAsync(downChannel, listenerContext);
        Task dialTask = dialer.DialAsync(downChannelFromProtocolPov, dialerContext);

        Assert.ThrowsAsync<Libp2pException>(async () => await dialTask,
            "Dialer should reject connection when responder identity key is unusable");

        listenTask.Wait(5000);
    }

    [Test]
    public void Test_DialerRejects_WhenResponderSignatureUsesWrongIdentity()
    {
        var maliciousListener = new MaliciousResponderProtocol(
            MaliciousResponderProtocol.CorruptionMode.WrongIdentity,
            new MultiplexerSettings());

        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
        dialerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        dialerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}" });

        TestChannel dialerUpChannel = new();
        dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        NoiseProtocol dialer = new();

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));
        listenerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(2)]);
        listenerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}" });

        TestChannel listenerUpChannel = new();
        listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        Task listenTask = maliciousListener.ListenAsync(downChannel, listenerContext);
        Task dialTask = dialer.DialAsync(downChannelFromProtocolPov, dialerContext);

        Assert.ThrowsAsync<Libp2pException>(async () => await dialTask,
            "Dialer should reject connection when responder signature is from a different identity");

        listenTask.Wait(5000);
    }

    [Test]
    public void Test_ListenerRejects_WhenInitiatorSignatureIsTampered()
    {
        var maliciousDialer = new MaliciousInitiatorProtocol(
            MaliciousInitiatorProtocol.CorruptionMode.TamperedSignature,
            new MultiplexerSettings());

        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));
        listenerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(2)]);
        listenerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}" });

        TestChannel listenerUpChannel = new();
        listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        NoiseProtocol listener = new();

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
        dialerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        dialerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}" });

        TestChannel dialerUpChannel = new();
        dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        Task listenTask = listener.ListenAsync(downChannel, listenerContext);
        Task dialTask = maliciousDialer.DialAsync(downChannelFromProtocolPov, dialerContext);

        Assert.ThrowsAsync<Libp2pException>(async () => await listenTask,
            "Listener should reject connection when initiator signature is tampered");

        dialTask.Wait(5000);
    }

    [Test]
    public void Test_ListenerRejects_WhenInitiatorSignatureIsEmpty()
    {
        var maliciousDialer = new MaliciousInitiatorProtocol(
            MaliciousInitiatorProtocol.CorruptionMode.EmptySignature,
            new MultiplexerSettings());

        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));
        listenerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(2)]);
        listenerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}" });

        TestChannel listenerUpChannel = new();
        listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        NoiseProtocol listener = new();

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
        dialerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        dialerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}" });

        TestChannel dialerUpChannel = new();
        dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        Task listenTask = listener.ListenAsync(downChannel, listenerContext);
        Task dialTask = maliciousDialer.DialAsync(downChannelFromProtocolPov, dialerContext);

        Assert.ThrowsAsync<Libp2pException>(async () => await listenTask,
            "Listener should reject connection when initiator signature is empty");

        dialTask.Wait(5000);
    }

    [Test]
    public void Test_ListenerRejects_WhenInitiatorSignatureUsesWrongIdentity()
    {
        var maliciousDialer = new MaliciousInitiatorProtocol(
            MaliciousInitiatorProtocol.CorruptionMode.WrongIdentity,
            new MultiplexerSettings());

        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));
        listenerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(2)]);
        listenerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}" });

        TestChannel listenerUpChannel = new();
        listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        NoiseProtocol listener = new();

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
        dialerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        dialerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}" });

        TestChannel dialerUpChannel = new();
        dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        Task listenTask = listener.ListenAsync(downChannel, listenerContext);
        Task dialTask = maliciousDialer.DialAsync(downChannelFromProtocolPov, dialerContext);

        Assert.ThrowsAsync<Libp2pException>(async () => await listenTask,
            "Listener should reject connection when initiator signature is from a different identity");

        dialTask.Wait(5000);
    }

    [Test]
    public void Test_ListenerRejects_WhenInitiatorIdentityKeyIsUnusable()
    {
        var maliciousDialer = new MaliciousInitiatorProtocol(
            MaliciousInitiatorProtocol.CorruptionMode.UnusableIdentityKey,
            new MultiplexerSettings());

        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));
        listenerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(2)]);
        listenerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}" });

        TestChannel listenerUpChannel = new();
        listenerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        NoiseProtocol listener = new();

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
        dialerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        dialerContext.State.Returns(new State() { RemoteAddress = $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}" });

        TestChannel dialerUpChannel = new();
        dialerContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        Task listenTask = listener.ListenAsync(downChannel, listenerContext);
        Task dialTask = maliciousDialer.DialAsync(downChannelFromProtocolPov, dialerContext);

        Assert.ThrowsAsync<Libp2pException>(async () => await listenTask,
            "Listener should reject connection when initiator identity key is unusable");

        dialTask.Wait(5000);
    }

    /// <remarks>
    /// The listener does not know who is connecting, so no expected peer ID stands
    /// in the way; only the key check stops the padded key's second peer ID.
    /// </remarks>
    [Test]
    public void Test_ListenerRejects_WhenInitiatorIdentityKeyIsPadded()
    {
        MaliciousInitiatorProtocol maliciousDialer = new(MaliciousInitiatorProtocol.CorruptionMode.PaddedIdentityKey, new MultiplexerSettings());
        (IConnectionContext listenerContext, State listenerState, _) = Context(TestPeers.Identity(2), "/ip4/0.0.0.0/tcp/0");
        (IConnectionContext dialerContext, _, _) = Context(TestPeers.Identity(1), $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}");
        IChannel downChannel = new TestChannel();

        Task listenTask = new NoiseProtocol().ListenAsync(downChannel, listenerContext);
        Task dialTask = maliciousDialer.DialAsync(((TestChannel)downChannel).Reverse(), dialerContext);

        Assert.ThrowsAsync<Libp2pException>(async () => await listenTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.That(listenerState.RemotePublicKey, Is.Null, "A rejected handshake must not store an identity.");
        dialTask.Wait(5000);
    }

    [Test]
    public void Test_DialerRejects_WhenResponderIdentityKeyIsPadded()
    {
        MaliciousResponderProtocol maliciousListener = new(MaliciousResponderProtocol.CorruptionMode.PaddedIdentityKey, new MultiplexerSettings());
        (IConnectionContext dialerContext, State dialerState, _) = Context(TestPeers.Identity(1), "/ip4/0.0.0.0/tcp/0");
        (IConnectionContext listenerContext, _, _) = Context(TestPeers.Identity(2), $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}");
        IChannel downChannel = new TestChannel();

        Task listenTask = maliciousListener.ListenAsync(downChannel, listenerContext);
        Task dialTask = new NoiseProtocol().DialAsync(((TestChannel)downChannel).Reverse(), dialerContext);

        Assert.ThrowsAsync<Libp2pException>(async () => await dialTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.That(dialerState.RemotePublicKey, Is.Null, "A rejected handshake must not store an identity.");
        listenTask.Wait(5000);
    }

    /// <remarks>
    /// The same keypair must reach the same peer ID whichever SEC1 encoding its
    /// owner sends, as it does in the plaintext handshake.
    /// </remarks>
    [Test]
    public async Task Test_RemoteIdentity_IsCompressed_WhenInitiatorSendsUncompressedSecp256k1Key()
    {
        Identity initiator = new(keyType: Core.Dto.KeyType.Secp256K1);
        MaliciousInitiatorProtocol uncompressedDialer = new(MaliciousInitiatorProtocol.CorruptionMode.UncompressedIdentityKey, new MultiplexerSettings());
        (IConnectionContext listenerContext, State listenerState, Task upgraded) = Context(TestPeers.Identity(2), "/ip4/0.0.0.0/tcp/0");
        (IConnectionContext dialerContext, _, _) = Context(initiator, $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(2)}");
        IChannel downChannel = new TestChannel();

        Task listenTask = new NoiseProtocol().ListenAsync(downChannel, listenerContext);
        await uncompressedDialer.DialAsync(((TestChannel)downChannel).Reverse(), dialerContext).WaitAsync(TimeSpan.FromSeconds(10));
        await upgraded.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Multiple(() =>
        {
            Assert.That(listenerState.RemotePublicKey, Is.EqualTo(initiator.PublicKey));
            Assert.That(listenerState.RemoteAddress!.GetPeerId(), Is.EqualTo(initiator.PeerId));
        });
        await downChannel.CloseAsync();
        await listenTask.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <remarks>
    /// The dialer knows the compressed peer ID it meant to reach, and the responder
    /// sends the same key uncompressed, which must still match that ID.
    /// </remarks>
    [Test]
    public async Task Test_DialerAccepts_WhenResponderSendsTheDialedSecp256k1KeyUncompressed()
    {
        Identity responder = new(keyType: Core.Dto.KeyType.Secp256K1);
        MaliciousResponderProtocol uncompressedListener = new(MaliciousResponderProtocol.CorruptionMode.UncompressedIdentityKey, new MultiplexerSettings());
        (IConnectionContext dialerContext, State dialerState, Task upgraded) = Context(TestPeers.Identity(1), $"/ip4/0.0.0.0/tcp/0/p2p/{responder.PeerId}");
        (IConnectionContext listenerContext, _, _) = Context(responder, $"/ip4/0.0.0.0/tcp/0/p2p/{TestPeers.PeerId(1)}");
        IChannel downChannel = new TestChannel();

        Task listenTask = uncompressedListener.ListenAsync(downChannel, listenerContext);
        Task dialTask = new NoiseProtocol().DialAsync(((TestChannel)downChannel).Reverse(), dialerContext);
        await upgraded.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(dialerState.RemotePublicKey, Is.EqualTo(responder.PublicKey));
        await downChannel.CloseAsync();
        await Task.WhenAll(listenTask, dialTask).WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// A connection context for <paramref name="identity"/> whose upgrade completes
    /// <c>Upgraded</c>, which happens only once the handshake has stored the remote identity.
    /// </summary>
    private static (IConnectionContext Context, State State, Task Upgraded) Context(Identity identity, string remoteAddress)
    {
        State state = new() { RemoteAddress = remoteAddress };
        TaskCompletionSource upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IConnectionContext context = Substitute.For<IConnectionContext>();
        context.Peer.Identity.Returns(identity);
        context.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(identity)]);
        context.State.Returns(state);
        context.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            upgraded.TrySetResult();
            return new TestChannel();
        });
        return (context, state, upgraded.Task);
    }

    private class MaliciousResponderProtocol : NoiseProtocol
    {
        public enum CorruptionMode { TamperedSignature, EmptySignature, WrongIdentity, UnusableIdentityKey, PaddedIdentityKey, UncompressedIdentityKey }

        private readonly CorruptionMode _mode;
        private readonly Protocol _protocol = new(HandshakePattern.XX, CipherFunction.ChaChaPoly, HashFunction.Sha256);
        private readonly Identity _rogueIdentity;

        public MaliciousResponderProtocol(CorruptionMode mode, MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null)
            : base(multiplexerSettings, loggerFactory)
        {
            _mode = mode;
            _rogueIdentity = new Identity();
        }

        public new async Task ListenAsync(IChannel downChannel, IConnectionContext context)
        {
            ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);

            KeyPair serverStatic = KeyPair.Generate();
            using HandshakeState handshakeState = _protocol.Create(false, s: serverStatic.PrivateKey);

            byte[] lenBytes = (await downChannel.ReadAsync(2).OrThrow()).ToArray();
            short len = BinaryPrimitives.ReadInt16BigEndian(lenBytes);
            byte[] buffer = new byte[Protocol.MaxMessageLength];
            ReadOnlySequence<byte> msg0Bytes = await downChannel.ReadAsync(len).OrThrow();
            handshakeState.ReadMessage(msg0Bytes.ToArray(), buffer);

            NoiseHandshakePayload payload;

            switch (_mode)
            {
                case CorruptionMode.TamperedSignature:
                    {
                        byte[] msg = Encoding.UTF8.GetBytes("noise-libp2p-static-key:")
                            .Concat(ByteString.CopyFrom(serverStatic.PublicKey))
                            .ToArray();
                        byte[] sig = context.Peer.Identity.Sign(msg);
                        byte[] tamperedSig = new byte[sig.Length];
                        Array.Copy(sig, tamperedSig, sig.Length);
                        tamperedSig[0] ^= 0xFF;
                        payload = new NoiseHandshakePayload
                        {
                            IdentityKey = context.Peer.Identity.PublicKey.ToByteString(),
                            IdentitySig = ByteString.CopyFrom(tamperedSig),
                        };
                    }
                    break;

                case CorruptionMode.EmptySignature:
                    payload = new NoiseHandshakePayload
                    {
                        IdentityKey = context.Peer.Identity.PublicKey.ToByteString(),
                        IdentitySig = ByteString.Empty,
                    };
                    break;

                case CorruptionMode.WrongIdentity:
                    {
                        byte[] msg = Encoding.UTF8.GetBytes("noise-libp2p-static-key:")
                            .Concat(ByteString.CopyFrom(serverStatic.PublicKey))
                            .ToArray();
                        byte[] sig = _rogueIdentity.Sign(msg);
                        payload = new NoiseHandshakePayload
                        {
                            IdentityKey = context.Peer.Identity.PublicKey.ToByteString(),
                            IdentitySig = ByteString.CopyFrom(sig),
                        };
                    }
                    break;

                case CorruptionMode.UnusableIdentityKey:
                    payload = UnusableIdentityKeyPayload();
                    break;

                case CorruptionMode.PaddedIdentityKey:
                    payload = SignedPayload(context.Peer.Identity, serverStatic.PublicKey, Padded(context.Peer.Identity.PublicKey));
                    break;

                case CorruptionMode.UncompressedIdentityKey:
                    payload = SignedPayload(context.Peer.Identity, serverStatic.PublicKey, Uncompressed(context.Peer.Identity.PublicKey));
                    break;

                default:
                    throw new InvalidOperationException();
            }

            buffer = new byte[Protocol.MaxMessageLength];
            (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg1 =
                handshakeState.WriteMessage(payload.ToByteArray(), buffer.AsSpan(2));
            BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(), (short)msg1.BytesWritten);
            await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg1.BytesWritten + 2));

            lenBytes = (await downChannel.ReadAsync(2).OrThrow()).ToArray();
            len = BinaryPrimitives.ReadInt16BigEndian(lenBytes);
            ReadOnlySequence<byte> hs2Bytes = await downChannel.ReadAsync(len).OrThrow();
            handshakeState.ReadMessage(hs2Bytes.ToArray(), buffer);
        }
    }

    private class MaliciousInitiatorProtocol : NoiseProtocol
    {
        public enum CorruptionMode { TamperedSignature, EmptySignature, WrongIdentity, UnusableIdentityKey, PaddedIdentityKey, UncompressedIdentityKey }

        private readonly CorruptionMode _mode;
        private readonly Protocol _protocol = new(HandshakePattern.XX, CipherFunction.ChaChaPoly, HashFunction.Sha256);
        private readonly Identity _rogueIdentity;

        public MaliciousInitiatorProtocol(CorruptionMode mode, MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null)
            : base(multiplexerSettings, loggerFactory)
        {
            _mode = mode;
            _rogueIdentity = new Identity();
        }

        public new async Task DialAsync(IChannel downChannel, IConnectionContext context)
        {
            ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);

            KeyPair clientStatic = KeyPair.Generate();
            using HandshakeState handshakeState = _protocol.Create(true, s: clientStatic.PrivateKey);
            byte[] buffer = new byte[Protocol.MaxMessageLength];

            (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg0 = handshakeState.WriteMessage(null, buffer);

            byte[] lenBytes = new byte[2];
            BinaryPrimitives.WriteInt16BigEndian(lenBytes.AsSpan(), (short)msg0.BytesWritten);
            await downChannel.WriteAsync(new ReadOnlySequence<byte>(lenBytes));
            await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg0.BytesWritten));

            lenBytes = (await downChannel.ReadAsync(2).OrThrow()).ToArray();
            int len = BinaryPrimitives.ReadInt16BigEndian(lenBytes.AsSpan());
            ReadOnlySequence<byte> received = await downChannel.ReadAsync(len).OrThrow();
            (int BytesRead, byte[] HandshakeHash, Transport Transport) msg1 =
                handshakeState.ReadMessage(received.ToArray(), buffer);
            NoiseHandshakePayload msg1Decoded = NoiseHandshakePayload.Parser.ParseFrom(buffer.AsSpan(0, msg1.BytesRead));

            if (msg1Decoded is null)
            {
                throw new Libp2pException("Bad handshake message has been received.");
            }

            NoiseHandshakePayload payload;

            switch (_mode)
            {
                case CorruptionMode.TamperedSignature:
                    {
                        byte[] msg = Encoding.UTF8.GetBytes("noise-libp2p-static-key:")
                            .Concat(ByteString.CopyFrom(clientStatic.PublicKey))
                            .ToArray();
                        byte[] sig = context.Peer.Identity.Sign(msg);
                        byte[] tamperedSig = new byte[sig.Length];
                        Array.Copy(sig, tamperedSig, sig.Length);
                        tamperedSig[0] ^= 0xFF;
                        payload = new NoiseHandshakePayload
                        {
                            IdentityKey = context.Peer.Identity.PublicKey.ToByteString(),
                            IdentitySig = ByteString.CopyFrom(tamperedSig),
                        };
                    }
                    break;

                case CorruptionMode.EmptySignature:
                    payload = new NoiseHandshakePayload
                    {
                        IdentityKey = context.Peer.Identity.PublicKey.ToByteString(),
                        IdentitySig = ByteString.Empty,
                    };
                    break;

                case CorruptionMode.WrongIdentity:
                    {
                        byte[] msg = Encoding.UTF8.GetBytes("noise-libp2p-static-key:")
                            .Concat(ByteString.CopyFrom(clientStatic.PublicKey))
                            .ToArray();
                        byte[] sig = _rogueIdentity.Sign(msg);
                        payload = new NoiseHandshakePayload
                        {
                            IdentityKey = context.Peer.Identity.PublicKey.ToByteString(),
                            IdentitySig = ByteString.CopyFrom(sig),
                        };
                    }
                    break;

                case CorruptionMode.UnusableIdentityKey:
                    payload = UnusableIdentityKeyPayload();
                    break;

                case CorruptionMode.PaddedIdentityKey:
                    payload = SignedPayload(context.Peer.Identity, clientStatic.PublicKey, Padded(context.Peer.Identity.PublicKey));
                    break;

                case CorruptionMode.UncompressedIdentityKey:
                    payload = SignedPayload(context.Peer.Identity, clientStatic.PublicKey, Uncompressed(context.Peer.Identity.PublicKey));
                    break;

                default:
                    throw new InvalidOperationException();
            }

            buffer = new byte[Protocol.MaxMessageLength];
            (int BytesWritten2, byte[] HandshakeHash, Transport Transport) msg2 =
                handshakeState.WriteMessage(payload.ToByteArray(), buffer);
            BinaryPrimitives.WriteInt16BigEndian(lenBytes.AsSpan(), (short)msg2.BytesWritten2);
            await downChannel.WriteAsync(new ReadOnlySequence<byte>(lenBytes));
            await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg2.BytesWritten2));
        }
    }

    /// <summary>
    /// A key of an unknown type with a non-empty signature, so the handshake gets as far as verifying it.
    /// </summary>
    private static NoiseHandshakePayload UnusableIdentityKeyPayload() => new()
    {
        IdentityKey = new Core.Dto.PublicKey { Type = (Core.Dto.KeyType)7, Data = ByteString.CopyFrom([1, 2, 3]) }.ToByteString(),
        IdentitySig = ByteString.CopyFrom(new byte[64]),
    };

    /// <summary>
    /// A valid signature by <paramref name="identity"/> over its Noise static key,
    /// sent with <paramref name="identityKey"/> as the identity key.
    /// </summary>
    private static NoiseHandshakePayload SignedPayload(Identity identity, byte[] staticKey, Core.Dto.PublicKey identityKey) => new()
    {
        IdentityKey = identityKey.ToByteString(),
        IdentitySig = ByteString.CopyFrom(identity.Sign([.. Encoding.UTF8.GetBytes("noise-libp2p-static-key:"), .. staticKey])),
    };

    /// <summary>
    /// An Ed25519 key with a byte appended: it verifies the same signatures but
    /// derives a different peer ID.
    /// </summary>
    private static Core.Dto.PublicKey Padded(Core.Dto.PublicKey key) =>
        new() { Type = key.Type, Data = ByteString.CopyFrom([.. key.Data.ToByteArray(), 0x00]) };

    private static Core.Dto.PublicKey Uncompressed(Core.Dto.PublicKey key) =>
        new() { Type = key.Type, Data = ByteString.CopyFrom(CustomNamedCurves.GetByName("secp256k1").Curve.DecodePoint(key.Data.ToByteArray()).GetEncoded(false)) };
}
