// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Google.Protobuf;
using Libp2p.Protocols.KadDht.Storage;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NUnit.Framework;

namespace Libp2p.Protocols.KadDht.Tests.Storage;

[TestFixture]
public class IpnsRecordTests
{
    private Identity _identity = null!;
    private PeerId _peerId = null!;

    [SetUp]
    public void SetUp()
    {
        _identity = new Identity();
        _peerId = _identity.PeerId;
    }

    #region IpnsRecordValidator Tests

    [Test]
    public void Validator_MatchesPrefix_TrueForIpnsKeys()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        Assert.That(IpnsRecordValidator.MatchesPrefix(key), Is.True);
    }

    [Test]
    public void Validator_MatchesPrefix_FalseForOtherKeys()
    {
        Assert.That(IpnsRecordValidator.MatchesPrefix("/pk/someid"u8), Is.False);
        Assert.That(IpnsRecordValidator.MatchesPrefix("/other/key"u8), Is.False);
    }

    [Test]
    public void Validator_MatchesPrefix_FalseForShortKey()
    {
        Assert.That(IpnsRecordValidator.MatchesPrefix("/ipn"u8), Is.False);
    }

    [Test]
    public void Validator_MatchesPrefix_FalseForExactPrefix()
    {
        // Must have content after the prefix
        Assert.That(IpnsRecordValidator.MatchesPrefix("/ipns/"u8), Is.False);
    }

    [Test]
    public void Validator_Validate_AcceptsValidRecord()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmTest123",
            sequence: 1,
            eol: DateTimeOffset.UtcNow.AddHours(1));

        var validator = IpnsRecordValidator.Instance;
        Assert.That(validator.Validate(key, value), Is.True);
    }

    [Test]
    public void Validator_Validate_AcceptsRecordWithCustomTtl()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmCustomTtl",
            sequence: 5,
            eol: DateTimeOffset.UtcNow.AddHours(2),
            ttl: TimeSpan.FromMinutes(30));

        Assert.That(IpnsRecordValidator.Instance.Validate(key, value), Is.True);
    }

    [Test]
    public void Validator_Validate_RejectsEmptyValue()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        Assert.That(IpnsRecordValidator.Instance.Validate(key, ReadOnlySpan<byte>.Empty), Is.False);
    }

    [Test]
    public void Validator_Validate_RejectsGarbageValue()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        Assert.That(IpnsRecordValidator.Instance.Validate(key, "not-a-protobuf"u8), Is.False);
    }

    [Test]
    public void Validator_Validate_RejectsExpiredRecord()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmExpired",
            sequence: 1,
            eol: DateTimeOffset.UtcNow.AddHours(-1)); // Already expired

        Assert.That(IpnsRecordValidator.Instance.Validate(key, value), Is.False);
    }

    [Test]
    public void Validator_Validate_RejectsWrongSignature()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmOriginal",
            sequence: 1,
            eol: DateTimeOffset.UtcNow.AddHours(1));

        // Parse, tamper with the signature, re-serialize
        var entry = IpnsEntry.Parser.ParseFrom(value);
        var tampered = entry.Clone();
        byte[] badSig = entry.SignatureV2.ToByteArray();
        badSig[0] ^= 0xFF; // Flip bits
        tampered.SignatureV2 = ByteString.CopyFrom(badSig);

        Assert.That(IpnsRecordValidator.Instance.Validate(key, tampered.ToByteArray()), Is.False);
    }

    [Test]
    public void Validator_Validate_RejectsTamperedData()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmOriginal",
            sequence: 1,
            eol: DateTimeOffset.UtcNow.AddHours(1));

        // Parse, tamper with the data field, re-serialize (signature won't match)
        var entry = IpnsEntry.Parser.ParseFrom(value);
        var tampered = entry.Clone();
        byte[] badData = entry.Data.ToByteArray();
        badData[^1] ^= 0xFF;
        tampered.Data = ByteString.CopyFrom(badData);

        Assert.That(IpnsRecordValidator.Instance.Validate(key, tampered.ToByteArray()), Is.False);
    }

    [Test]
    public void Validator_Validate_RejectsWrongPeerId()
    {
        // Create record signed by _identity but stored under a different PeerId
        var otherIdentity = new Identity();
        byte[] wrongKey = IpnsRecordHelper.BuildIpnsKey(otherIdentity.PeerId);
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmTest",
            sequence: 1,
            eol: DateTimeOffset.UtcNow.AddHours(1));

        Assert.That(IpnsRecordValidator.Instance.Validate(wrongKey, value), Is.False);
    }

    [Test]
    public void Validator_Validate_RejectsNonIpnsKey()
    {
        byte[] key = "/pk/someid"u8.ToArray();
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmTest",
            sequence: 1,
            eol: DateTimeOffset.UtcNow.AddHours(1));

        Assert.That(IpnsRecordValidator.Instance.Validate(key, value), Is.False);
    }

    [Test]
    public void Validator_Select_ReturnsHighestSequence()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);

        var values = new List<byte[]>
        {
            IpnsRecordHelper.CreateSignedRecord(_identity, "/ipfs/QmOld", 1, DateTimeOffset.UtcNow.AddHours(1)),
            IpnsRecordHelper.CreateSignedRecord(_identity, "/ipfs/QmNewest", 10, DateTimeOffset.UtcNow.AddHours(1)),
            IpnsRecordHelper.CreateSignedRecord(_identity, "/ipfs/QmMiddle", 5, DateTimeOffset.UtcNow.AddHours(1)),
        };

        int selected = IpnsRecordValidator.Instance.Select(key, values);
        Assert.That(selected, Is.EqualTo(1)); // Index 1 has sequence=10
    }

    [Test]
    public void Validator_Select_ReturnsMinusOneForEmpty()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        Assert.That(IpnsRecordValidator.Instance.Select(key, Array.Empty<byte[]>()), Is.EqualTo(-1));
    }

    [Test]
    public void Validator_Select_SkipsInvalidEntries()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);

        var values = new List<byte[]>
        {
            "garbage"u8.ToArray(),
            IpnsRecordHelper.CreateSignedRecord(_identity, "/ipfs/QmValid", 3, DateTimeOffset.UtcNow.AddHours(1)),
        };

        Assert.That(IpnsRecordValidator.Instance.Select(key, values), Is.EqualTo(1));
    }

    [Test]
    public void Validator_Select_SingleRecord()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);

        var values = new List<byte[]>
        {
            IpnsRecordHelper.CreateSignedRecord(_identity, "/ipfs/QmOnly", 1, DateTimeOffset.UtcNow.AddHours(1)),
        };

        Assert.That(IpnsRecordValidator.Instance.Select(key, values), Is.EqualTo(0));
    }

    #endregion

    #region IpnsRecordHelper Tests

    [Test]
    public void Helper_BuildIpnsKey_StartsWithPrefix()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        Assert.That(key.AsSpan(0, 6).SequenceEqual("/ipns/"u8), Is.True);
    }

    [Test]
    public void Helper_BuildIpnsKey_ContainsPeerIdBytes()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        Assert.That(key.AsSpan(6).SequenceEqual(_peerId.Bytes), Is.True);
    }

    [Test]
    public void Helper_BuildIpnsKey_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => IpnsRecordHelper.BuildIpnsKey(null!));
    }

    [Test]
    public void Helper_CreateSignedRecord_ThrowsOnNullIdentity()
    {
        Assert.Throws<ArgumentNullException>(() =>
            IpnsRecordHelper.CreateSignedRecord(null!, "/ipfs/Qm", 1, DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Test]
    public void Helper_CreateSignedRecord_ThrowsOnEmptyPath()
    {
        Assert.Throws<ArgumentException>(() =>
            IpnsRecordHelper.CreateSignedRecord(_identity, "", 1, DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Test]
    public void Helper_CreateSignedRecord_ThrowsWithoutPrivateKey()
    {
        var pubOnlyIdentity = new Identity(_identity.PublicKey);
        Assert.Throws<InvalidOperationException>(() =>
            IpnsRecordHelper.CreateSignedRecord(pubOnlyIdentity, "/ipfs/Qm", 1, DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Test]
    public void Helper_CreateSignedRecord_ProducesValidProtobuf()
    {
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmTest",
            sequence: 42,
            eol: DateTimeOffset.UtcNow.AddHours(1));

        var entry = IpnsEntry.Parser.ParseFrom(value);

        Assert.Multiple(() =>
        {
            Assert.That(entry.HasValue, Is.True);
            Assert.That(entry.HasSignatureV1, Is.True);
            Assert.That(entry.HasSignatureV2, Is.True);
            Assert.That(entry.HasData, Is.True);
            Assert.That(entry.HasValidity, Is.True);
            Assert.That(entry.HasPubKey, Is.True);
            Assert.That(entry.Sequence, Is.EqualTo(42));
            Assert.That(entry.ValidityType, Is.EqualTo(IpnsEntry.Types.ValidityType.Eol));
            Assert.That(Encoding.UTF8.GetString(entry.Value.Span), Is.EqualTo("/ipfs/QmTest"));
        });
    }

    [Test]
    public void Helper_CreateSignedRecord_RoundTrip_ValidateSucceeds()
    {
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmRoundTrip",
            sequence: 100,
            eol: DateTimeOffset.UtcNow.AddHours(24));

        Assert.That(IpnsRecordValidator.Instance.Validate(key, value), Is.True);
    }

    [Test]
    public void Helper_GetContentPath_ReturnsCorrectPath()
    {
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmContentPath",
            sequence: 1,
            eol: DateTimeOffset.UtcNow.AddHours(1));

        string? path = IpnsRecordHelper.GetContentPath(value);
        Assert.That(path, Is.EqualTo("/ipfs/QmContentPath"));
    }

    [Test]
    public void Helper_GetContentPath_ReturnsNullForGarbage()
    {
        Assert.That(IpnsRecordHelper.GetContentPath("not-protobuf"u8), Is.Null);
    }

    [Test]
    public void Helper_GetSequence_ReturnsCorrectValue()
    {
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmSeq",
            sequence: 42,
            eol: DateTimeOffset.UtcNow.AddHours(1));

        ulong? seq = IpnsRecordHelper.GetSequence(value);
        Assert.That(seq, Is.EqualTo(42));
    }

    [Test]
    public void Helper_GetSequence_ReturnsNullForGarbage()
    {
        Assert.That(IpnsRecordHelper.GetSequence("not-protobuf"u8), Is.Null);
    }

    [Test]
    public void Helper_CreateSignedRecord_IncludesPublicKey()
    {
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/Qm",
            sequence: 1,
            eol: DateTimeOffset.UtcNow.AddHours(1));

        var entry = IpnsEntry.Parser.ParseFrom(value);
        Assert.That(entry.HasPubKey, Is.True);
        Assert.That(entry.PubKey.IsEmpty, Is.False);
    }

    [Test]
    public void Helper_CreateSignedRecord_DifferentSequencesProduceDifferentSignatures()
    {
        var eol = DateTimeOffset.UtcNow.AddHours(1);
        byte[] value1 = IpnsRecordHelper.CreateSignedRecord(_identity, "/ipfs/Qm", 1, eol);
        byte[] value2 = IpnsRecordHelper.CreateSignedRecord(_identity, "/ipfs/Qm", 2, eol);

        var entry1 = IpnsEntry.Parser.ParseFrom(value1);
        var entry2 = IpnsEntry.Parser.ParseFrom(value2);

        Assert.That(entry1.SignatureV2.Span.SequenceEqual(entry2.SignatureV2.Span), Is.False);
    }

    #endregion

    #region CompositeRecordValidator IPNS integration

    [Test]
    public void CompositeValidator_CreateDefault_ValidatesIpnsRecord()
    {
        var validator = CompositeRecordValidator.CreateDefault();
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);
        byte[] value = IpnsRecordHelper.CreateSignedRecord(
            _identity,
            "/ipfs/QmComposite",
            sequence: 1,
            eol: DateTimeOffset.UtcNow.AddHours(1));

        Assert.That(validator.Validate(key, value), Is.True);
    }

    [Test]
    public void CompositeValidator_CreateDefault_RejectsInvalidIpnsRecord()
    {
        var validator = CompositeRecordValidator.CreateDefault();
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);

        Assert.That(validator.Validate(key, "invalid-ipns-record"u8), Is.False);
    }

    [Test]
    public void CompositeValidator_SelectsHighestSequenceForIpns()
    {
        var validator = CompositeRecordValidator.CreateDefault();
        byte[] key = IpnsRecordHelper.BuildIpnsKey(_peerId);

        var values = new List<byte[]>
        {
            IpnsRecordHelper.CreateSignedRecord(_identity, "/ipfs/QmOld", 1, DateTimeOffset.UtcNow.AddHours(1)),
            IpnsRecordHelper.CreateSignedRecord(_identity, "/ipfs/QmNew", 5, DateTimeOffset.UtcNow.AddHours(1)),
        };

        Assert.That(validator.Select(key, values), Is.EqualTo(1));
    }

    #endregion
}
