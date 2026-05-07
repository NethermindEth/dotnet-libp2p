// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Noise.Dto;
using Noise;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class NoiseProtocolTests
{
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

    private class MaliciousResponderProtocol : NoiseProtocol
    {
        public enum CorruptionMode { TamperedSignature, EmptySignature, WrongIdentity }

        private readonly CorruptionMode _mode;
        private readonly Protocol _protocol = new(HandshakePattern.XX, CipherFunction.ChaChaPoly, HashFunction.Sha256);
        private readonly Identity _rogueIdentity;

        public MaliciousResponderProtocol(CorruptionMode mode, MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null)
            : base(multiplexerSettings, loggerFactory)
        {
            _mode = mode;
            _rogueIdentity = new Identity();
        }

        public override async Task ListenAsync(IChannel downChannel, IConnectionContext context)
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
        public enum CorruptionMode { TamperedSignature, EmptySignature, WrongIdentity }

        private readonly CorruptionMode _mode;
        private readonly Protocol _protocol = new(HandshakePattern.XX, CipherFunction.ChaChaPoly, HashFunction.Sha256);
        private readonly Identity _rogueIdentity;

        public MaliciousInitiatorProtocol(CorruptionMode mode, MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null)
            : base(multiplexerSettings, loggerFactory)
        {
            _mode = mode;
            _rogueIdentity = new Identity();
        }

        public override async Task DialAsync(IChannel downChannel, IConnectionContext context)
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

                default:
                    throw new InvalidOperationException();
            }

            buffer = new byte[Protocol.MaxMessageLength];
            (int BytesWritten2, byte[] HandshakeHash, Transport Transport) msg2 =
                handshakeState.WriteMessage(payload.ToByteArray(), buffer);
            BinaryPrimitives.WriteInt16BigEndian(lenBytes.AsSpan(), (short)msg2.BytesWritten);
            await downChannel.WriteAsync(new ReadOnlySequence<byte>(lenBytes));
            await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg2.BytesWritten));
        }
    }
}
