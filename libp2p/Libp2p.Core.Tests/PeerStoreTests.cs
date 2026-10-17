// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;

namespace Nethermind.Libp2p.Core.Tests;

/// <remarks>
/// Gossipsub passes peer-exchange records from remote peers straight to
/// <see cref="PeerStore.Discover(ByteString)"/>, so a bad record must be ignored
/// rather than throw out of the RPC handler.
/// </remarks>
[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class PeerStoreTests
{
    [Test]
    public void Test_Discover_AnnouncesPeer_WhenRecordIsValid()
    {
        Identity peer = new();

        Assert.That(Announcements(SignedRecord(peer)), Is.EqualTo(1));
    }

    [TestCaseSource(nameof(UnusableKeys))]
    public void Test_Discover_IgnoresRecord_WhenKeyIsUnusable(PublicKey key)
    {
        SignedEnvelope envelope = SignedEnvelope.Parser.ParseFrom(SignedRecord(new Identity()));
        envelope.PublicKey = key.ToByteString();

        Assert.That(Announcements(envelope.ToByteString()), Is.Zero);
    }

    public static IEnumerable<TestCaseData> UnusableKeys()
    {
        yield return new TestCaseData(Key((KeyType)7, [1, 2, 3])).SetName("Test_Discover_IgnoresRecord_WhenKeyIsOfUnknownType");
        yield return new TestCaseData(Key(KeyType.Ed25519, new byte[31])).SetName("Test_Discover_IgnoresRecord_WhenEd25519KeyIsTruncated");
        yield return new TestCaseData(Key(KeyType.Secp256K1, [0x00])).SetName("Test_Discover_IgnoresRecord_WhenSecp256k1KeyIsThePointAtInfinity");
        yield return new TestCaseData(Key(KeyType.Rsa, [1, 2, 3, 4])).SetName("Test_Discover_IgnoresRecord_WhenRsaKeyIsGarbage");
    }

    private static int Announcements(ByteString signedRecord)
    {
        PeerStore store = new();
        int announced = 0;
        store.OnNewPeer += _ => announced++;

        store.Discover(signedRecord);

        return announced;
    }

    private static ByteString SignedRecord(Identity peer) =>
        SigningHelper.CreateSignedEnvelope(peer, [Multiaddress.Decode($"/ip4/127.0.0.1/tcp/4001/p2p/{peer.PeerId}")], 1);

    private static PublicKey Key(KeyType keyType, byte[] data) =>
        new() { Type = keyType, Data = ByteString.CopyFrom(data) };
}
