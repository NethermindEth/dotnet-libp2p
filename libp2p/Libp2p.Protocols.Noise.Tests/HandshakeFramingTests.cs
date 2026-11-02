// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Noise.Dto;
using Noise;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

public class HandshakeFramingTests
{
    // Message 0 and 2 are read by ListenAsync; message 1 is read by DialAsync.
    [TestCase(0, 0x7fff, false)]
    [TestCase(0, 0x8000, false)]
    [TestCase(0, 0xffff, false)]
    [TestCase(1, 0x7fff, false)]
    [TestCase(1, 0x8000, false)]
    [TestCase(1, 0xffff, false)]
    [TestCase(2, 0x7fff, false)]
    [TestCase(2, 0x8000, false)]
    [TestCase(2, 0xffff, false)]
    [TestCase(0, 0x8000, true)]
    [TestCase(1, 0x8000, true)]
    [TestCase(2, 0x8000, true)]
    public async Task AcceptsUnsignedHandshakeLength(int message, int length, bool fragmented)
    {
        Assert.That(Protocol.MaxMessageLength, Is.EqualTo(ushort.MaxValue), "Every two-byte length fits the dependency's limit.");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using HandshakePeer peer = new(message);
        try
        {
            await peer.PrepareAsync(deadline.Token);
            byte[] frame = peer.Frame(peer.Payload(length));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame), Is.EqualTo(length));

            // Coalesce the next encrypted record with the last handshake message. It must
            // remain unread until the handshake has authenticated the peer and upgraded.
            byte[] next = message == 2 ? peer.DataFrame() : [];
            Task write = peer.SendAsync([.. frame, .. next], fragmented, deadline.Token);
            if (message != 2)
            {
                await peer.ReceiveAsync(deadline.Token);
                if (message == 0)
                    await peer.SendAsync(peer.Frame(peer.Payload()), false, deadline.Token);
            }
            await write.WaitAsync(deadline.Token);
            await peer.Upgraded.Task.WaitAsync(deadline.Token);
            Assert.That(peer.Context.State.RemotePeerId, Is.EqualTo(TestPeers.PeerId(2)));
            if (message != 2)
                write = peer.SendAsync(peer.DataFrame(), false, deadline.Token);
            Assert.That((await peer.Up.Reverse.ReadAsync(3, token: deadline.Token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 9, 8, 7 }));
            await write.WaitAsync(deadline.Token);
        }
        finally
        {
            await peer.CloseAsync();
        }
    }

    [TestCase(0, true)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(2, false)]
    public async Task TruncatedHandshakeDoesNotUpgrade(int message, bool truncatedPrefix)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using HandshakePeer peer = new(message);
        try
        {
            await peer.PrepareAsync(deadline.Token);
            byte[] frame = peer.Frame(peer.Payload(0x8000));
            await peer.SendAsync(frame[..(truncatedPrefix ? 1 : frame.Length - 1)], true, deadline.Token);
            await peer.Wire.WriteEofAsync(deadline.Token);
            Assert.ThrowsAsync<ChannelClosedException>(async () => await peer.Subject.WaitAsync(deadline.Token));
            Assert.That(peer.Upgraded.Task.IsCompleted, Is.False);
            Assert.That(peer.Context.State.RemotePublicKey, Is.Null);
        }
        finally
        {
            await peer.CloseAsync(allowRejection: true);
        }
    }

    [TestCase(0, true)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(2, false)]
    public async Task ConnectionCancellationStopsPartialHandshakeReads(int message, bool partialPrefix)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource connection = new();
        using HandshakePeer peer = new(message, connection.Token);
        try
        {
            await peer.PrepareAsync(deadline.Token);
            byte[] frame = peer.Frame(peer.Payload(0x8000));
            // Await consumption of a prefix fragment or the first payload byte, proving
            // that the handshake has reached its blocked read before cancellation.
            await peer.SendAsync(frame[..(partialPrefix ? 1 : 3)], true, deadline.Token);
            Assert.That(peer.Subject.IsCompleted, Is.False);
            connection.Cancel();
            Assert.ThrowsAsync<ChannelClosedException>(async () => await peer.Subject.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(peer.Upgraded.Task.IsCompleted, Is.False);
            Assert.That(peer.Context.State.RemotePublicKey, Is.Null);
        }
        finally
        {
            await peer.CloseAsync(allowRejection: true);
        }
    }

    private sealed class HandshakePeer : IDisposable
    {
        private readonly int _message;
        private readonly KeyPair _key = KeyPair.Generate();
        private readonly HandshakeState _handshake;
        private Transport? _transport;
        private readonly Channel _down = new();
        public Channel Up { get; } = new();
        public IChannel Wire => _down.Reverse;
        public IConnectionContext Context { get; } = Substitute.For<IConnectionContext>();
        public TaskCompletionSource Upgraded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Subject { get; }

        public HandshakePeer(int message, CancellationToken connectionToken = default)
        {
            _message = message;
            Protocol protocol = new(HandshakePattern.XX, CipherFunction.ChaChaPoly, HashFunction.Sha256);
            _handshake = protocol.Create(message != 1, s: _key.PrivateKey);
            Context.Peer.Identity.Returns(TestPeers.Identity(1));
            Context.Token.Returns(connectionToken);
            Context.State.Returns(new State { RemoteAddress = $"/ip4/127.0.0.1/tcp/0/p2p/{TestPeers.PeerId(2)}" });
            Context.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
            {
                Upgraded.TrySetResult();
                return Up;
            });
            Subject = message == 1
                ? new NoiseProtocol().DialAsync(_down, Context)
                : new NoiseProtocol().ListenAsync(_down, Context);
        }

        public async Task PrepareAsync(CancellationToken token)
        {
            if (_message == 2)
                await SendAsync(Frame([]), false, token);
            if (_message != 0)
                await ReceiveAsync(token);
        }

        public byte[] Payload(int? wireLength = null)
        {
            int overhead = _message == 0 ? 32 : _message == 1 ? 96 : 64;
            if (_message == 0 && _transport is null && wireLength.HasValue)
                return new byte[wireLength.Value - overhead];
            NoiseHandshakePayload payload = new()
            {
                IdentityKey = TestPeers.Identity(2).PublicKey.ToByteString(),
                IdentitySig = ByteString.CopyFrom(TestPeers.Identity(2).Sign([.. "noise-libp2p-static-key:"u8, .. _key.PublicKey]))
            };
            byte[] encoded = payload.ToByteArray();
            if (!wireLength.HasValue) return encoded;
            // An unknown length-delimited protobuf field is legal extension data.
            int padding = wireLength.Value - overhead - encoded.Length - 4;
            using MemoryStream stream = new();
            stream.Write(encoded);
            using (CodedOutputStream output = new(stream, leaveOpen: true))
            {
                output.WriteTag(15, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(new byte[padding]));
            }
            return stream.ToArray();
        }

        public byte[] Frame(byte[] payload)
        {
            byte[] frame = new byte[Protocol.MaxMessageLength + 2];
            var result = _handshake.WriteMessage(payload, frame.AsSpan(2));
            _transport = result.Transport;
            BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)result.BytesWritten));
            return frame[..(result.BytesWritten + 2)];
        }

        public async Task ReceiveAsync(CancellationToken token)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian((await Wire.ReadAsync(2, token: token).OrThrow()).ToArray());
            byte[] buffer = new byte[Protocol.MaxMessageLength];
            var result = _handshake.ReadMessage((await Wire.ReadAsync(length, token: token).OrThrow()).ToArray(), buffer);
            _transport = result.Transport;
        }

        public byte[] DataFrame()
        {
            byte[] frame = new byte[21];
            int length = _transport!.WriteMessage([9, 8, 7], frame.AsSpan(2));
            BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)length));
            return frame;
        }

        public async Task SendAsync(byte[] bytes, bool fragmented, CancellationToken token)
        {
            for (int offset = 0; offset < bytes.Length;)
            {
                int count = fragmented ? Math.Min(offset < 2 ? 1 : 997, bytes.Length - offset) : bytes.Length;
                await Wire.WriteAsync(new ReadOnlySequence<byte>(bytes, offset, count), token).OrThrow();
                offset += count;
            }
        }

        public async Task CloseAsync(bool allowRejection = false)
        {
            await Up.CloseAsync();
            await _down.CloseAsync();
            try { await Subject.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (ChannelClosedException) when (allowRejection) { }
        }

        public void Dispose()
        {
            _transport?.Dispose();
            _handshake.Dispose();
            _key.Dispose();
        }
    }
}
