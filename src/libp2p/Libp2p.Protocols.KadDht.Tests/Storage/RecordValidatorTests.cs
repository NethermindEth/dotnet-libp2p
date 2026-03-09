// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using Libp2p.Protocols.KadDht.Storage;
using NUnit.Framework;

namespace Libp2p.Protocols.KadDht.Tests.Storage;

[TestFixture]
public class RecordValidatorTests
{
    #region DefaultRecordValidator Tests

    [Test]
    public void DefaultValidator_Validate_AcceptsNonEmptyValue()
    {
        var validator = DefaultRecordValidator.Instance;
        Assert.That(validator.Validate("key"u8, "value"u8), Is.True);
    }

    [Test]
    public void DefaultValidator_Validate_RejectsEmptyValue()
    {
        var validator = DefaultRecordValidator.Instance;
        Assert.That(validator.Validate("key"u8, ReadOnlySpan<byte>.Empty), Is.False);
    }

    [Test]
    public void DefaultValidator_Select_ReturnsLastRecord()
    {
        var validator = DefaultRecordValidator.Instance;
        var values = new List<byte[]>
        {
            "first"u8.ToArray(),
            "second"u8.ToArray(),
            "third"u8.ToArray()
        };
        Assert.That(validator.Select("key"u8, values), Is.EqualTo(2));
    }

    [Test]
    public void DefaultValidator_Select_ReturnsMinusOneForEmpty()
    {
        var validator = DefaultRecordValidator.Instance;
        Assert.That(validator.Select("key"u8, Array.Empty<byte[]>()), Is.EqualTo(-1));
    }

    #endregion

    #region PublicKeyRecordValidator Tests

    [Test]
    public void PublicKeyValidator_MatchesPrefix_TrueForPkKeys()
    {
        byte[] key = "/pk/somepeerid"u8.ToArray();
        Assert.That(PublicKeyRecordValidator.MatchesPrefix(key), Is.True);
    }

    [Test]
    public void PublicKeyValidator_MatchesPrefix_FalseForOtherKeys()
    {
        byte[] key = "/other/key"u8.ToArray();
        Assert.That(PublicKeyRecordValidator.MatchesPrefix(key), Is.False);
    }

    [Test]
    public void PublicKeyValidator_MatchesPrefix_FalseForShortKey()
    {
        byte[] key = "/p"u8.ToArray();
        Assert.That(PublicKeyRecordValidator.MatchesPrefix(key), Is.False);
    }

    [Test]
    public void PublicKeyValidator_Validate_AcceptsValidPublicKeyRecord()
    {
        var validator = PublicKeyRecordValidator.Instance;

        // Create a "public key" value
        byte[] publicKeyValue = Encoding.UTF8.GetBytes("test-public-key-data-12345");

        // Compute expected PeerId = SHA-256(publicKeyValue)
        byte[] peerIdBytes = SHA256.HashData(publicKeyValue);

        // Key = /pk/{PeerId}
        byte[] prefix = "/pk/"u8.ToArray();
        byte[] key = new byte[prefix.Length + peerIdBytes.Length];
        prefix.CopyTo(key, 0);
        peerIdBytes.CopyTo(key, prefix.Length);

        Assert.That(validator.Validate(key, publicKeyValue), Is.True);
    }

    [Test]
    public void PublicKeyValidator_Validate_RejectsWrongPublicKey()
    {
        var validator = PublicKeyRecordValidator.Instance;

        byte[] publicKeyValue = Encoding.UTF8.GetBytes("test-public-key");
        byte[] wrongPeerId = SHA256.HashData(Encoding.UTF8.GetBytes("different-key"));

        byte[] prefix = "/pk/"u8.ToArray();
        byte[] key = new byte[prefix.Length + wrongPeerId.Length];
        prefix.CopyTo(key, 0);
        wrongPeerId.CopyTo(key, prefix.Length);

        Assert.That(validator.Validate(key, publicKeyValue), Is.False);
    }

    [Test]
    public void PublicKeyValidator_Validate_RejectsEmptyValue()
    {
        var validator = PublicKeyRecordValidator.Instance;
        byte[] key = "/pk/someid"u8.ToArray();
        Assert.That(validator.Validate(key, ReadOnlySpan<byte>.Empty), Is.False);
    }

    [Test]
    public void PublicKeyValidator_Validate_RejectsNonPkKey()
    {
        var validator = PublicKeyRecordValidator.Instance;
        byte[] key = "/other/key"u8.ToArray();
        byte[] value = "somevalue"u8.ToArray();
        Assert.That(validator.Validate(key, value), Is.False);
    }

    [Test]
    public void PublicKeyValidator_Select_ReturnsFirstValidRecord()
    {
        var validator = PublicKeyRecordValidator.Instance;

        byte[] publicKeyValue = Encoding.UTF8.GetBytes("correct-public-key");
        byte[] peerIdBytes = SHA256.HashData(publicKeyValue);
        byte[] prefix = "/pk/"u8.ToArray();
        byte[] key = new byte[prefix.Length + peerIdBytes.Length];
        prefix.CopyTo(key, 0);
        peerIdBytes.CopyTo(key, prefix.Length);

        var values = new List<byte[]>
        {
            "wrong-key"u8.ToArray(),
            publicKeyValue,
            "another-wrong"u8.ToArray()
        };

        Assert.That(validator.Select(key, values), Is.EqualTo(1));
    }

    [Test]
    public void PublicKeyValidator_Select_ReturnsMinusOneWhenNoneValid()
    {
        var validator = PublicKeyRecordValidator.Instance;
        byte[] key = "/pk/invalidpeerid"u8.ToArray();

        var values = new List<byte[]>
        {
            "wrong1"u8.ToArray(),
            "wrong2"u8.ToArray()
        };

        Assert.That(validator.Select(key, values), Is.EqualTo(-1));
    }

    #endregion

    #region CompositeRecordValidator Tests

    [Test]
    public void CompositeValidator_CreateDefault_ContainsPkValidator()
    {
        var validator = CompositeRecordValidator.CreateDefault();

        // Create a valid /pk/ record
        byte[] publicKeyValue = Encoding.UTF8.GetBytes("my-public-key-for-composite-test");
        byte[] peerIdBytes = SHA256.HashData(publicKeyValue);
        byte[] prefix = "/pk/"u8.ToArray();
        byte[] key = new byte[prefix.Length + peerIdBytes.Length];
        prefix.CopyTo(key, 0);
        peerIdBytes.CopyTo(key, prefix.Length);

        Assert.That(validator.Validate(key, publicKeyValue), Is.True);
    }

    [Test]
    public void CompositeValidator_CreateDefault_RejectsInvalidPkRecord()
    {
        var validator = CompositeRecordValidator.CreateDefault();

        byte[] wrongPeerId = SHA256.HashData("different"u8);
        byte[] prefix = "/pk/"u8.ToArray();
        byte[] key = new byte[prefix.Length + wrongPeerId.Length];
        prefix.CopyTo(key, 0);
        wrongPeerId.CopyTo(key, prefix.Length);

        // Should reject because value doesn't hash to the PeerId
        Assert.That(validator.Validate(key, "wrong-value"u8), Is.False);
    }

    [Test]
    public void CompositeValidator_FallsBackToDefaultForUnknownPrefix()
    {
        var validator = CompositeRecordValidator.CreateDefault();

        byte[] key = "/custom/mykey"u8.ToArray();
        byte[] value = "myvalue"u8.ToArray();

        // Default validator accepts all non-empty values
        Assert.That(validator.Validate(key, value), Is.True);
    }

    [Test]
    public void CompositeValidator_FallbackRejectsEmptyValue()
    {
        var validator = CompositeRecordValidator.CreateDefault();

        byte[] key = "/custom/mykey"u8.ToArray();

        // Default validator rejects empty values
        Assert.That(validator.Validate(key, ReadOnlySpan<byte>.Empty), Is.False);
    }

    [Test]
    public void CompositeValidator_Select_DelegatesToCorrectValidator()
    {
        var validator = CompositeRecordValidator.CreateDefault();

        // For non-pk keys, should use default validator (returns last)
        byte[] key = "/custom/key"u8.ToArray();
        var values = new List<byte[]>
        {
            "first"u8.ToArray(),
            "second"u8.ToArray(),
            "third"u8.ToArray()
        };

        Assert.That(validator.Select(key, values), Is.EqualTo(2));
    }

    [Test]
    public void CompositeValidator_CustomPrefixValidator()
    {
        // Create a custom validator that only accepts values starting with "OK"
        var customValidator = new TestPrefixValidator();

        var composite = new CompositeRecordValidator(
            new (byte[], IRecordValidator)[]
            {
                ("/test/"u8.ToArray(), customValidator),
                (PublicKeyRecordValidator.Prefix, PublicKeyRecordValidator.Instance)
            });

        Assert.That(composite.Validate("/test/key"u8, "OK-value"u8), Is.True);
        Assert.That(composite.Validate("/test/key"u8, "BAD-value"u8), Is.False);
        // Unknown prefix falls back to default
        Assert.That(composite.Validate("/unknown/key"u8, "anything"u8), Is.True);
    }

    private sealed class TestPrefixValidator : IRecordValidator
    {
        public bool Validate(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value) =>
            value.Length >= 2 && value[0] == (byte)'O' && value[1] == (byte)'K';

        public int Select(ReadOnlySpan<byte> key, IReadOnlyList<byte[]> values) =>
            values.Count > 0 ? 0 : -1;
    }

    #endregion
}
