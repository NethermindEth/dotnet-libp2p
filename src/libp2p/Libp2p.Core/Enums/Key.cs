namespace Nethermind.Libp2p.Core.Enums;
public enum Key
{
    // 128-bit AES symmetric key
    // draft
    Aes128 = 0xa0,
    // 192-bit AES symmetric key
    // draft
    Aes192 = 0xa1,
    // 256-bit AES symmetric key
    // draft
    Aes256 = 0xa2,
    // 128-bit ChaCha symmetric key
    // draft
    Chacha128 = 0xa3,
    // 256-bit ChaCha symmetric key
    // draft
    Chacha256 = 0xa4,
    // Secp256k1 public key (compressed)
    // draft
    Secp256k1Pub = 0xe7,
    // BLS12-381 public key in the G1 field
    // draft
    Bls12_381G1Pub = 0xea,
    // BLS12-381 public key in the G2 field
    // draft
    Bls12_381G2Pub = 0xeb,
    // Curve25519 public key
    // draft
    X25519Pub = 0xec,
    // Ed25519 public key
    // draft
    Ed25519Pub = 0xed,
    // BLS12-381 concatenated public keys in both the G1 and G2 fields
    // draft
    Bls12_381G1g2Pub = 0xee,
    // Sr25519 public key
    // draft
    Sr25519Pub = 0xef,
    // P-256 public Key (compressed)
    // draft
    P256Pub = 0x1200,
    // P-384 public Key (compressed)
    // draft
    P384Pub = 0x1201,
    // P-521 public Key (compressed)
    // draft
    P521Pub = 0x1202,
    // Ed448 public Key
    // draft
    Ed448Pub = 0x1203,
    // X448 public Key
    // draft
    X448Pub = 0x1204,
    // RSA public key. DER-encoded ASN.1 type RSAPublicKey according to IETF RFC 8017 (PKCS #1)
    // draft
    RsaPub = 0x1205,
    // SM2 public key (compressed)
    // draft
    Sm2Pub = 0x1206,
    // Ed25519 private key
    // draft
    Ed25519Priv = 0x1300,
    // Secp256k1 private key
    // draft
    Secp256k1Priv = 0x1301,
    // Curve25519 private key
    // draft
    X25519Priv = 0x1302,
    // Sr25519 private key
    // draft
    Sr25519Priv = 0x1303,
    // RSA private key
    // draft
    RsaPriv = 0x1305,
    // P-256 private key
    // draft
    P256Priv = 0x1306,
    // P-384 private key
    // draft
    P384Priv = 0x1307,
    // P-521 private key
    // draft
    P521Priv = 0x1308,
    // JSON object containing only the required members of a JWK (RFC 7518 and RFC 7517) representing the public key. Serialisation based on JCS (RFC 8785)
    // draft
    Jwk_jcsPub = 0xeb51,
    Unknown,
}
