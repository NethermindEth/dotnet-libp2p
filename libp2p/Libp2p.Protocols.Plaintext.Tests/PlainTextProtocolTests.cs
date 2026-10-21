// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.PlainText.Dto;
using NSubstitute;
using NUnit.Framework;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Math.EC;
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




    /// <remarks>
    /// The exchange is self-consistent, so only the key decode can reject it.
    /// This is what proves the handshake goes through <see cref="PublicKeyDecoder"/>
    /// rather than parsing the key itself.
    /// </remarks>
    [Test]
    public void Test_Handshake_Fails_WhenKeyMaterialIsUnusable()
    {
        PublicKey truncated = new()
        {
            Type = KeyType.Ed25519,
            Data = ByteString.CopyFrom(new byte[1]),
        };
        Exchange exchange = new()
        {
            Id = ByteString.CopyFrom(new PeerId(truncated).Bytes),
            Pubkey = truncated.ToByteString(),
        };

        AssertHandshakeRejected(new State(), exchange);
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

    /// <remarks>
    /// go-libp2p derives the peer ID from the compressed key whichever encoding
    /// arrives, so a peer sending its key uncompressed or hybrid still claims the
    /// compressed key's ID.
    /// </remarks>
    [TestCase(false, false, TestName = "Test_RemoteIdentity_IsCompressed_WhenSecp256k1KeyArrivesUncompressed(dialer)")]
    [TestCase(false, true, TestName = "Test_RemoteIdentity_IsCompressed_WhenSecp256k1KeyArrivesUncompressed(listener)")]
    [TestCase(true, false, TestName = "Test_RemoteIdentity_IsCompressed_WhenSecp256k1KeyArrivesHybrid(dialer)")]
    [TestCase(true, true, TestName = "Test_RemoteIdentity_IsCompressed_WhenSecp256k1KeyArrivesHybrid(listener)")]
    public async Task Test_RemoteIdentity_IsCompressed_WhenSecp256k1KeyArrivesExpanded(bool hybrid, bool isListener)
    {
        Identity remote = new(keyType: KeyType.Secp256K1);
        State state = new() { RemoteAddress = "/ip4/127.0.0.1/tcp/0" };

        await HandshakeAsync(state, ExpandedExchange(remote, hybrid), isListener);

        Assert.Multiple(() =>
        {
            Assert.That(state.RemotePublicKey, Is.EqualTo(remote.PublicKey));
            Assert.That(state.RemoteAddress!.GetPeerId(), Is.EqualTo(remote.PeerId));
        });
    }

    [Test]
    public async Task Test_Handshake_Succeeds_WhenDialedSecp256k1PeerSendsItsKeyUncompressed()
    {
        Identity remote = new(keyType: KeyType.Secp256K1);
        State state = new() { RemoteAddress = $"/ip4/127.0.0.1/tcp/0/p2p/{remote.PeerId}" };

        await HandshakeAsync(state, ExpandedExchange(remote, hybrid: false));

        Assert.That(state.RemotePublicKey, Is.EqualTo(remote.PublicKey));
    }

    /// <remarks>
    /// Only the length is sent. Rejecting it must not wait for a body that never
    /// comes, and a length past <see cref="int.MaxValue"/> must not wrap to a
    /// negative read size.
    /// </remarks>
    [TestCase(65_537ul, false, TestName = "Test_Handshake_Fails_WhenExchangeLengthIsOneOverTheLimit(dialer)")]
    [TestCase(65_537ul, true, TestName = "Test_Handshake_Fails_WhenExchangeLengthIsOneOverTheLimit(listener)")]
    [TestCase(1ul << 31, false, TestName = "Test_Handshake_Fails_WhenExchangeLengthDoesNotFitInInt(dialer)")]
    [TestCase(1ul << 31, true, TestName = "Test_Handshake_Fails_WhenExchangeLengthDoesNotFitInInt(listener)")]
    [TestCase(1ul << 40, false, TestName = "Test_Handshake_Fails_WhenExchangeLengthNeedsSixVarintBytes(dialer)")]
    [TestCase(1ul << 40, true, TestName = "Test_Handshake_Fails_WhenExchangeLengthNeedsSixVarintBytes(listener)")]
    [TestCase(ulong.MaxValue, false, TestName = "Test_Handshake_Fails_WhenExchangeLengthIsTheLargestVarint(dialer)")]
    [TestCase(ulong.MaxValue, true, TestName = "Test_Handshake_Fails_WhenExchangeLengthIsTheLargestVarint(listener)")]
    public void Test_Handshake_Fails_WhenExchangeLengthIsTooLarge(ulong length, bool isListener)
    {
        AssertRawReplyRejected(Varint(length), isListener);
    }

    /// <remarks>
    /// Eleven continuation bytes run past the longest varint; ten bytes ending in
    /// 0x7f are the right length but overflow 64 bits.
    /// </remarks>
    [TestCase(11, 0xff, false, TestName = "Test_Handshake_Fails_WhenExchangeLengthIsTooLongForAVarint(dialer)")]
    [TestCase(11, 0xff, true, TestName = "Test_Handshake_Fails_WhenExchangeLengthIsTooLongForAVarint(listener)")]
    [TestCase(9, 0x7f, false, TestName = "Test_Handshake_Fails_WhenExchangeLengthOverflowsSixtyFourBits(dialer)")]
    [TestCase(9, 0x7f, true, TestName = "Test_Handshake_Fails_WhenExchangeLengthOverflowsSixtyFourBits(listener)")]
    public void Test_Handshake_Fails_WhenExchangeLengthIsNotAVarint(int continuationBytes, byte lastByte, bool isListener)
    {
        byte[] reply = [.. Enumerable.Repeat((byte)0xff, continuationBytes), .. continuationBytes == 11 ? Array.Empty<byte>() : [lastByte]];

        AssertRawReplyRejected(reply, isListener);
    }

    [Test]
    public void Test_Handshake_Fails_WhenExchangeIsEmpty()
    {
        AssertRawReplyRejected([0x00]);
    }

    /// <remarks>
    /// The channel reports the hang-up as <see cref="ChannelClosedException"/>,
    /// which is itself a <see cref="Libp2pException"/>.
    /// </remarks>
    [Test]
    public void Test_Handshake_Fails_WhenStreamEndsBeforeTheAnnouncedLength()
    {
        AssertRawReplyRejected([.. Varint(100), .. new byte[10]], closeAfterReply: true, expected: typeof(ChannelClosedException));
    }

    /// <remarks>
    /// Pads a valid exchange with an unknown field so it is exactly the limit.
    /// </remarks>
    [TestCase(false, TestName = "Test_RemoteIdentity_IsStored_WhenExchangeIsExactlyTheLimit(dialer)")]
    [TestCase(true, TestName = "Test_RemoteIdentity_IsStored_WhenExchangeIsExactlyTheLimit(listener)")]
    public async Task Test_RemoteIdentity_IsStored_WhenExchangeIsExactlyTheLimit(bool isListener)
    {
        Identity remote = TestPeers.Identity(RemotePeerIndex);
        byte[] exchange = ValidExchange(remote).ToByteArray();
        const byte unknownFieldTag = (15 << 3) | 2;
        int paddingLength = (1 << 16) - exchange.Length - 1 - 3;
        byte[] padded = [.. exchange, unknownFieldTag, .. Varint((ulong)paddingLength), .. new byte[paddingLength]];
        Assert.That(padded, Has.Length.EqualTo(1 << 16), "precondition: the exchange is exactly 64 KiB");
        State state = new();

        await HandshakeAsync(state, padded, isListener);

        Assert.That(state.RemotePublicKey, Is.EqualTo(remote.PublicKey));
    }

    [Test]
    public async Task Test_Handshake_Succeeds_WhenEarlierHandshakeStoredTheSameSecp256k1Key()
    {
        Identity remote = new(keyType: KeyType.Secp256K1);
        State state = new() { RemotePublicKey = remote.PublicKey };

        await HandshakeAsync(state, ExpandedExchange(remote, hybrid: false));

        Assert.That(state.RemotePublicKey, Is.EqualTo(remote.PublicKey));
    }

    /// <remarks>
    /// The second peer ID that the expanded bytes used to derive must not be
    /// claimable.
    /// </remarks>
    [TestCase(false, TestName = "Test_Handshake_Fails_WhenIdIsDerivedFromUncompressedSecp256k1Key")]
    [TestCase(true, TestName = "Test_Handshake_Fails_WhenIdIsDerivedFromHybridSecp256k1Key")]
    public void Test_Handshake_Fails_WhenIdIsDerivedFromExpandedSecp256k1Key(bool hybrid)
    {
        PublicKey expanded = Expanded(new Identity(keyType: KeyType.Secp256K1).PublicKey, hybrid);
        Exchange exchange = new()
        {
            Id = ByteString.CopyFrom(new PeerId(expanded).Bytes),
            Pubkey = expanded.ToByteString(),
        };

        AssertHandshakeRejected(new State(), exchange);
    }

    [Test]
    public void Test_Handshake_Fails_WhenDialedAddressNamesTheUncompressedSecp256k1PeerId()
    {
        Identity remote = new(keyType: KeyType.Secp256K1);
        PeerId uncompressedPeerId = new(Expanded(remote.PublicKey, hybrid: false));
        State state = new() { RemoteAddress = $"/ip4/127.0.0.1/tcp/0/p2p/{uncompressedPeerId}" };

        AssertHandshakeRejected(state, ExpandedExchange(remote, hybrid: false));
    }
    private static Exchange ValidExchange(Identity identity) => new()
    {
        Id = ByteString.CopyFrom(identity.PeerId.Bytes),
        Pubkey = identity.PublicKey.ToByteString(),
    };

    /// <summary>
    /// The remote's key in a 65-byte encoding, claiming its normal (compressed) peer ID.
    /// </summary>
    private static Exchange ExpandedExchange(Identity remote, bool hybrid) => new()
    {
        Id = ByteString.CopyFrom(remote.PeerId.Bytes),
        Pubkey = Expanded(remote.PublicKey, hybrid).ToByteString(),
    };

    private static PublicKey Expanded(PublicKey compressed, bool hybrid)
    {
        ECPoint point = CustomNamedCurves.GetByName("secp256k1").Curve.DecodePoint(compressed.Data.ToByteArray()).Normalize();
        byte[] encoded = point.GetEncoded(false);
        if (hybrid)
        {
            encoded[0] = (byte)(point.AffineYCoord.ToBigInteger().TestBit(0) ? 0x07 : 0x06);
        }

        return new PublicKey { Type = KeyType.Secp256K1, Data = ByteString.CopyFrom(encoded) };
    }


    private static void AssertHandshakeRejected(State state, Exchange exchange, bool isListener = false) =>
        AssertHandshakeRejected(state, exchange.ToByteArray(), isListener);

    private static void AssertHandshakeRejected(State state, byte[] payload, bool isListener = false)
    {
        PublicKey? identityBeforeHandshake = state.RemotePublicKey;

        Assert.ThrowsAsync<Libp2pException>(async () => await HandshakeAsync(state, payload, isListener));
        Assert.That(state.RemotePublicKey, Is.EqualTo(identityBeforeHandshake), "A rejected handshake must not change the stored identity.");
    }

    /// <remarks>
    /// Checks the exact exception type, so a lower-level exception that merely
    /// derives from <see cref="Libp2pException"/> cannot stand in for the
    /// handshake's own error.
    /// </remarks>
    private static void AssertRawReplyRejected(byte[] reply, bool isListener = false, bool closeAfterReply = false, Type? expected = null)
    {
        State state = new();

        Assert.ThrowsAsync(expected ?? typeof(Libp2pException), async () => await HandshakeWithRawReplyAsync(state, reply, isListener, closeAfterReply));
        Assert.That(state.RemotePublicKey, Is.Null, "A rejected handshake must not store an identity.");
    }

    private static Task HandshakeAsync(State state, Exchange exchange, bool isListener = false) =>
        HandshakeAsync(state, exchange.ToByteArray(), isListener);

    private static Task HandshakeAsync(State state, byte[] payload, bool isListener = false) =>
        HandshakeWithRawReplyAsync(state, [.. Varint((ulong)payload.Length), .. payload], isListener);

    /// <summary>
    /// Runs one side of /plaintext/2.0.0 against a peer that answers with <paramref name="reply"/> as-is,
    /// optionally closing its end afterwards. Channel writes block until the other end reads, so the
    /// remote peer has to run concurrently.
    /// </summary>
    private static async Task HandshakeWithRawReplyAsync(State state, byte[] reply, bool isListener = false, bool closeAfterReply = false)
    {
        TestChannel channel = new();
        IChannel remoteEnd = channel.Reverse();

        IConnectionContext context = Substitute.For<IConnectionContext>();
        context.Peer.Identity.Returns(TestPeers.Identity(LocalPeerIndex));
        context.State.Returns(state);

        Task remote = Task.Run(async () =>
        {
            await ReadLengthPrefixedAsync(remoteEnd);
            await remoteEnd.WriteAsync(new ReadOnlySequence<byte>(reply));
            if (closeAfterReply)
            {
                await remoteEnd.CloseAsync();
            }
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

    private static byte[] Varint(ulong value)
    {
        List<byte> bytes = [];
        do
        {
            byte next = (byte)(value & 0x7f);
            value >>= 7;
            bytes.Add(value == 0 ? next : (byte)(next | 0x80));
        } while (value != 0);

        return [.. bytes];
    }

    private static async Task<byte[]> ReadLengthPrefixedAsync(IChannel channel)
    {
        int size = await channel.ReadVarintAsync();
        return (await channel.ReadAsync(size).OrThrow()).ToArray();
    }
}
