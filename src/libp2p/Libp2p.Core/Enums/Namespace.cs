namespace Nethermind.Libp2p.Core.Enums;
public enum Namespace
{
    // Namespace for string paths. Corresponds to `/` in ASCII.
    Path = 0x2f,
    // Ceramic Stream Id
    // draft
    Streamid = 0xce,
    // IPLD path
    // draft
    Ipld = 0xe2,
    // IPFS path
    // draft
    Ipfs = 0xe3,
    // Swarm path
    // draft
    Swarm = 0xe4,
    // IPNS path
    // draft
    Ipns = 0xe5,
    // ZeroNet site address
    // draft
    Zeronet = 0xe6,
    // DNSLink path
    Dnslink = 0xe8,
    // Skynet Namespace
    // draft
    SkynetNs = 0xb19910,
    // Arweave Namespace
    // draft
    ArweaveNs = 0xb29910,
    // Subspace Network Namespace
    // draft
    SubspaceNs = 0xb39910,
    // Kumandra Network Namespace
    // draft
    KumandraNs = 0xb49910,
    Unknown,
}
