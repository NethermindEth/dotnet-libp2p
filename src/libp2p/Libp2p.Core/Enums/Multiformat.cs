namespace Libp2p.Core.Enums;
public enum Multiformat
{
    // draft
    Multicodec = 0x30,
    // draft
    Multihash = 0x31,
    // draft
    Multiaddr = 0x32,
    // draft
    Multibase = 0x33,
    // CAIP-50 multi-chain account id
    // draft
    Caip50 = 0xca,
    // Compact encoding for Decentralized Identifers
    // draft
    Multidid = 0x0d1d,
    Unknown,
}
