namespace Nethermind.Libp2p.Core.Enums;

public enum Holochain
{
    // Holochain v0 address    + 8 R-S (63 x Base-32)
    // draft
    HolochainAdrV0 = 0x807124,
    // Holochain v1 address    + 8 R-S (63 x Base-32)
    // draft
    HolochainAdrV1 = 0x817124,
    // Holochain v0 public key + 8 R-S (63 x Base-32)
    // draft
    HolochainKeyV0 = 0x947124,
    // Holochain v1 public key + 8 R-S (63 x Base-32)
    // draft
    HolochainKeyV1 = 0x957124,
    // Holochain v0 signature  + 8 R-S (63 x Base-32)
    // draft
    HolochainSigV0 = 0xa27124,
    // Holochain v1 signature  + 8 R-S (63 x Base-32)
    // draft
    HolochainSigV1 = 0xa37124,
    Unknown,
}
