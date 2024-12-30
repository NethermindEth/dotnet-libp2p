namespace Nethermind.Libp2p.Core.Enums;
public enum Varsig
{
    // Namespace for all not yet standard signature algorithms
    // draft
    Varsig = 0xd000,
    // ES256K Signature Algorithm (secp256k1)
    // draft
    Es256k = 0xd0e7,
    // G1 signature for BLS-12381-G2
    // draft
    Bls12381G1Sig = 0xd0ea,
    // G2 signature for BLS-12381-G1
    // draft
    Bls12381G2Sig = 0xd0eb,
    // Edwards-Curve Digital Signature Algorithm
    // draft
    Eddsa = 0xd0ed,
    // EIP-191 Ethereum Signed Data Standard
    // draft
    Eip191 = 0xd191,
    // ES256 Signature Algorithm
    // draft
    Es256 = 0xd01200,
    // ES384 Signature Algorithm
    // draft
    Es284 = 0xd01201,
    // ES512 Signature Algorithm
    // draft
    Es512 = 0xd01202,
    // RS256 Signature Algorithm
    // draft
    Rs256 = 0xd01205,
    Unknown,
}
