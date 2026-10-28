// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>Shared accounting for retained provider payload, including UTF-16 dictionary keys.</summary>
internal static class ProviderRecordLimits
{
    internal const int DefaultMaxProviderKeys = 10_000;
    internal const int DefaultMaxAddressesPerProvider = 16;
    internal const int DefaultMaxProviderRecordBytes = 1024;

    internal static void Validate(int maxProvidersPerKey, int maxProviderKeys, int maxAddressesPerProvider, int maxProviderRecordBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProvidersPerKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProviderKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAddressesPerProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProviderRecordBytes);
    }

    // Base58 uses at least one character per input byte (including leading zeroes).
    // Keep this cheap length-only check before copying or Base64/Base58 conversion:
    // PeerId(byte[]) accepts attacker-sized arrays, making Base58 conversion potentially expensive.
    // TryGetAddressBudget is authoritative and subsumes these rejections after conversion.
    internal static bool CanFitIdentity(int keyLength, int peerIdLength, int budget) =>
        KeyBytes(keyLength) + 3L * peerIdLength <= budget;

    internal static bool TryGetAddressBudget(int keyLength, int peerIdLength, string peerIdKey, int budget, out long remaining)
    {
        remaining = budget - KeyBytes(keyLength) - peerIdLength - 2L * peerIdKey.Length;
        return remaining >= 0;
    }

    internal static bool TryRetainAddress(string address, ref long remaining)
    {
        long bytes = 2L * address.Length;
        if (bytes > remaining) return false;
        remaining -= bytes;
        return true;
    }

    private static long KeyBytes(int length) => 2 * (((long)length + 2) / 3 * 4);
}
