// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>
/// Validates DHT records during retrieval and before storage per the libp2p DHT spec.
/// Implementations should be pure functions (no side effects).
/// </summary>
public interface IRecordValidator
{
    /// <summary>
    /// Validate a record, returning true if it's valid (e.g., not expired, correctly signed).
    /// Called on GET_VALUE retrieval and before PUT_VALUE storage.
    /// </summary>
    bool Validate(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);

    /// <summary>
    /// Select the best record index from a set of candidate values.
    /// Decisions must be stable (deterministic for the same inputs).
    /// </summary>
    /// <returns>Index of the best value, or -1 if none are valid.</returns>
    int Select(ReadOnlySpan<byte> key, IReadOnlyList<byte[]> values);
}

/// <summary>
/// Default validator that accepts all records and selects the last (newest) one.
/// Suitable for timestamp-based conflict resolution.
/// </summary>
public sealed class DefaultRecordValidator : IRecordValidator
{
    public static readonly DefaultRecordValidator Instance = new();

    public bool Validate(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value) => value.Length > 0;

    public int Select(ReadOnlySpan<byte> key, IReadOnlyList<byte[]> values)
    {
        if (values.Count == 0) return -1;
        return values.Count - 1;
    }
}
