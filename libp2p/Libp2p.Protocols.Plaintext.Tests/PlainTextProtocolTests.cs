// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.PlainText.Dto;
using NSubstitute;
using NUnit.Framework;
using KeyType = Nethermind.Libp2p.Core.Dto.KeyType;
using PublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;

namespace Nethermind.Libp2p.Protocols.Plaintext.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class PlainTextProtocolTests
{
    private const int LocalPeerIndex = 1;
    private const int RemotePeerIndex = 2;
    private const int ImpostorPeerIndex = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestCase(false)]
    [TestCase(true)]
    public async Task Test_RemoteIdentity_IsStored_WhenExchangeIsValid(bool isListener)
    {
        Identity remote = TestPeers.Identity(RemotePeerIndex);
        State state = new() { RemoteAddress = $"/ip4/127.0.0.1/tcp/0/p2p/{remote.PeerId}" };

        await HandshakeAsync(state, ValidExchange(remote), isListener);

        Assert.That(state.RemotePublicKey, Is.EqualTo(remote.PublicKey));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Test_RemotePeerId_IsAppended_WhenAddressHasNone(bool isListener)
    {
        Identity remote = TestPeers.Identity(RemotePeerIndex);
        State state = new() { RemoteAddress = "/ip4/127.0.0.1/tcp/0" };

        await HandshakeAsync(state, ValidExchange(remote), isListener);

        Assert.Multiple(() =>
        {
            Assert.That(state.RemotePublicKey, Is.EqualTo(remote.PublicKey));
            Assert.That(state.RemoteAddress!.GetPeerId(), Is.EqualTo(remote.PeerId));
        });
    }

    [Test]
    public void Test_Handshake_Fails_WhenIdIsMissing()
    {
        Exchange exchange = new() { Pubkey = TestPeers.Identity(RemotePeerIndex).PublicKey.ToByteString() };

        AssertHandshakeRejected(new State(), exchange);
    }

    [Test]
    public void Test_Handshake_Fails_WhenPublicKeyIsEmpty()
    {
        Exchange exchange = new() { Pubkey = ByteString.Empty };

        AssertHandshakeRejected(new State(), exchange);
    }

    [Test]
    public void Test_Handshake_Fails_WhenPublicKeyCarriesNoKeyMaterial()
    {
        PublicKey emptyEd25519Key = new() { Type = KeyType.Ed25519, Data = ByteString.Empty };
        Exchange exchange = new() { Pubkey = emptyEd25519Key.ToByteString() };

        AssertHandshakeRejected(new State(), exchange);
    }

    [Test]
    public void Test_Handshake_Fails_WhenPublicKeyTypeIsUnknown()
    {
        PublicKey unknownTypeKey = new() { Type = (KeyType)7, Data = ByteString.CopyFrom([1, 2, 3, 4]) };
        Exchange exchange = new() { Pubkey = unknownTypeKey.ToByteString() };

        AssertHandshakeRejected(new State(), exchange);
    }

    [TestCase(1, TestName = "Test_Handshake_Fails_WhenEd25519KeyIsOneByte")]
    [TestCase(31, TestName = "Test_Handshake_Fails_WhenEd25519KeyIsTruncated")]
    [TestCase(33, TestName = "Test_Handshake_Fails_WhenEd25519KeyIsOverlong")]
    public void Test_Handshake_Fails_WhenEd25519KeyMaterialIsWrongLength(int length)
    {
        PublicKey key = new()
        {
            Type = KeyType.Ed25519,
            Data = ByteString.CopyFrom(new byte[length]),
        };

        AssertHandshakeRejected(new State(), SelfConsistentExchange(key));
    }

    [TestCase(KeyType.Secp256K1)]
    [TestCase(KeyType.Rsa)]
    [TestCase(KeyType.Ecdsa)]
    public void Test_Handshake_Fails_WhenKeyMaterialDoesNotParse(KeyType keyType)
    {
        PublicKey key = new()
        {
            Type = keyType,
            Data = ByteString.CopyFrom([1, 2, 3, 4]),
        };

        AssertHandshakeRejected(new State(), SelfConsistentExchange(key));
    }

    [Test]
    public void Test_Handshake_Fails_WhenPublicKeyIsMissing()
    {
        Identity remote = TestPeers.Identity(RemotePeerIndex);
        Exchange exchange = new() { Id = ByteString.CopyFrom(remote.PeerId.Bytes) };

        AssertHandshakeRejected(new State(), exchange);
    }

    [Test]
    public void Test_Handshake_Fails_WhenPublicKeyIsMalformed()
    {
        Exchange exchange = new() { Pubkey = ByteString.CopyFrom([0xff, 0xff, 0xff]) };

        AssertHandshakeRejected(new State(), exchange);
    }

    [Test]
    public void Test_Handshake_Fails_WhenExchangeIsNotParsable()
    {
        AssertHandshakeRejected(new State(), [0xff, 0xff, 0xff, 0xff]);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Test_Handshake_Fails_WhenIdDoesNotMatchPublicKey(bool isListener)
    {
        Exchange exchange = new()
        {
            Id = ByteString.CopyFrom(TestPeers.Identity(ImpostorPeerIndex).PeerId.Bytes),
            Pubkey = TestPeers.Identity(RemotePeerIndex).PublicKey.ToByteString(),
        };

        AssertHandshakeRejected(new State(), exchange, isListener);
    }

    [Test]
    public void Test_Handshake_Fails_WhenIdentityDoesNotMatchDialedPeer()
    {
        State state = new() { RemoteAddress = $"/ip4/127.0.0.1/tcp/0/p2p/{TestPeers.PeerId(ImpostorPeerIndex)}" };

        AssertHandshakeRejected(state, ValidExchange(TestPeers.Identity(RemotePeerIndex)));
    }

    [Test]
    public void Test_Handshake_Fails_WhenIdentityContradictsAnEarlierHandshake()
    {
        State state = new() { RemotePublicKey = TestPeers.Identity(ImpostorPeerIndex).PublicKey };

        AssertHandshakeRejected(state, ValidExchange(TestPeers.Identity(RemotePeerIndex)));
    }

    private static Exchange ValidExchange(Identity identity) => new()
    {
        Id = ByteString.CopyFrom(identity.PeerId.Bytes),
        Pubkey = identity.PublicKey.ToByteString(),
    };

    /// <summary>
    /// Build an exchange whose id is derived from the given key, so the id
    /// check cannot reject it and only the key material can.
    /// </summary>
    private static Exchange SelfConsistentExchange(PublicKey publicKey) => new()
    {
        Id = ByteString.CopyFrom(new PeerId(publicKey).Bytes),
        Pubkey = publicKey.ToByteString(),
    };

    private static void AssertHandshakeRejected(State state, Exchange exchange, bool isListener = false) =>
        AssertHandshakeRejected(state, exchange.ToByteArray(), isListener);

    private static void AssertHandshakeRejected(State state, byte[] payload, bool isListener = false)
    {
        PublicKey? identityBeforeHandshake = state.RemotePublicKey;

        Assert.ThrowsAsync<Libp2pException>(async () => await HandshakeAsync(state, payload, isListener));
        Assert.That(state.RemotePublicKey, Is.EqualTo(identityBeforeHandshake), "A rejected handshake must not change the stored identity.");
    }

    private static Task HandshakeAsync(State state, Exchange exchange, bool isListener = false) =>
        HandshakeAsync(state, exchange.ToByteArray(), isListener);

    /// <summary>
    /// Runs one side of /plaintext/2.0.0 against a peer that answers with <paramref name="payload"/>.
    /// Channel writes block until the other end reads, so the remote peer has to run concurrently.
    /// </summary>
    private static async Task HandshakeAsync(State state, byte[] payload, bool isListener = false)
    {
        TestChannel channel = new();
        IChannel remoteEnd = channel.Reverse();

        IConnectionContext context = Substitute.For<IConnectionContext>();
        context.Peer.Identity.Returns(TestPeers.Identity(LocalPeerIndex));
        context.State.Returns(state);

        Task remote = Task.Run(async () =>
        {
            await ReadLengthPrefixedAsync(remoteEnd);
            await WriteLengthPrefixedAsync(remoteEnd, payload);
        });

        PlainTextProtocol protocol = new();

        try
        {
            Task handshake = isListener ? protocol.ListenAsync(channel, context) : protocol.DialAsync(channel, context);
            await handshake.WaitAsync(Timeout);
        }
        finally
        {
            await channel.CloseAsync();
            await remote.WaitAsync(Timeout).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private static async Task WriteLengthPrefixedAsync(IChannel channel, byte[] payload)
    {
        byte[] sizeBuf = new byte[VarInt.GetSizeInBytes(payload.Length)];
        int offset = 0;
        VarInt.Encode(payload.Length, sizeBuf, ref offset);
        await channel.WriteAsync(new ReadOnlySequence<byte>([.. sizeBuf, .. payload]));
    }

    private static async Task<byte[]> ReadLengthPrefixedAsync(IChannel channel)
    {
        int size = await channel.ReadVarintAsync();
        return (await channel.ReadAsync(size).OrThrow()).ToArray();
    }
}
