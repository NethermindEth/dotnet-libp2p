namespace Nethermind.Libp2p.Core.Enums;

public enum Encryption
{
    // AES Galois/Counter Mode with 256-bit key and 12-byte IV
    // draft
    AesGcm256 = 0x2000,
    Unknown,
}
