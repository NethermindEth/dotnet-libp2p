namespace Nethermind.Libp2p.Core.Enums;
public enum Hash
{
    // The first 64-bits of a murmur3-x64-128 - used for UnixFS directory sharding.
    Murmur3X6464 = 0x22,
    // draft
    Murmur332 = 0x23,
    // CRC-32 non-cryptographic hash algorithm (IEEE 802.3)
    // draft
    Crc32 = 0x0132,
    // CRC-64 non-cryptographic hash algorithm (ECMA-182 - Annex B)
    // draft
    Crc64Ecma = 0x0164,
    // draft
    Murmur3X64128 = 0x1022,
    // Extremely fast non-cryptographic hash algorithm
    // draft
    Xxh32 = 0xb3e1,
    // Extremely fast non-cryptographic hash algorithm
    // draft
    Xxh64 = 0xb3e2,
    // Extremely fast non-cryptographic hash algorithm
    // draft
    Xxh364 = 0xb3e3,
    // Extremely fast non-cryptographic hash algorithm
    // draft
    Xxh3128 = 0xb3e4,
    Unknown,
}
