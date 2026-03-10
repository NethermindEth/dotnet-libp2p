// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>
/// Routes record validation to the appropriate validator based on key prefix.
/// </summary>
public sealed class CompositeRecordValidator : IRecordValidator
{
    private readonly IReadOnlyList<(byte[] Prefix, IRecordValidator Validator)> _prefixValidators;
    private readonly IRecordValidator _fallback;

    /// <summary>
    /// Creates a composite validator with the given prefix-to-validator mappings.
    /// Prefixes are matched in order; the first match wins.
    /// </summary>
    /// <param name="prefixValidators">Ordered list of (prefix, validator) pairs.</param>
    /// <param name="fallback">Validator used when no prefix matches. Defaults to <see cref="DefaultRecordValidator"/>.</param>
    public CompositeRecordValidator(
        IEnumerable<(byte[] Prefix, IRecordValidator Validator)> prefixValidators,
        IRecordValidator? fallback = null)
    {
        _prefixValidators = prefixValidators?.ToArray()
            ?? throw new ArgumentNullException(nameof(prefixValidators));
        _fallback = fallback ?? DefaultRecordValidator.Instance;
    }

    /// <summary>
    /// Creates a composite validator with the standard libp2p validators pre-registered:
    /// /pk/ → <see cref="PublicKeyRecordValidator"/>,
    /// /ipns/ → <see cref="IpnsRecordValidator"/>.
    /// </summary>
    public static CompositeRecordValidator CreateDefault(IRecordValidator? fallback = null)
    {
        return new CompositeRecordValidator(
            new (byte[], IRecordValidator)[]
            {
                (PublicKeyRecordValidator.Prefix, PublicKeyRecordValidator.Instance),
                (IpnsRecordValidator.Prefix, IpnsRecordValidator.Instance)
            },
            fallback ?? DefaultRecordValidator.Instance);
    }

    public bool Validate(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        return ResolveValidator(key).Validate(key, value);
    }

    public int Select(ReadOnlySpan<byte> key, IReadOnlyList<byte[]> values)
    {
        return ResolveValidator(key).Select(key, values);
    }

    private IRecordValidator ResolveValidator(ReadOnlySpan<byte> key)
    {
        foreach (var (prefix, validator) in _prefixValidators)
        {
            if (key.Length >= prefix.Length && key[..prefix.Length].SequenceEqual(prefix))
                return validator;
        }
        return _fallback;
    }
}
