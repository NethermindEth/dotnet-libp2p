namespace Nethermind.Libp2p.Core.Enums;
public enum Multiaddr
{
    Ip4 = 0x04,
    Tcp = 0x06,
    // draft
    Dccp = 0x21,
    Ip6 = 0x29,
    // draft
    Ip6zone = 0x2a,
    // CIDR mask for IP addresses
    // draft
    Ipcidr = 0x2b,
    Dns = 0x35,
    Dns4 = 0x36,
    Dns6 = 0x37,
    Dnsaddr = 0x38,
    // draft
    Sctp = 0x84,
    // draft
    Udp = 0x0111,
    // draft
    P2pWebrtcStar = 0x0113,
    // draft
    P2pWebrtcDirect = 0x0114,
    // draft
    P2pStardust = 0x0115,
    // WebRTC
    // draft
    Webrtc = 0x0118,
    P2pCircuit = 0x0122,
    // draft
    Udt = 0x012d,
    // draft
    Utp = 0x012e,
    Unix = 0x0190,
    // Textile Thread
    // draft
    Thread = 0x0196,
    // libp2p
    P2p = 0x01a5,
    // draft
    Https = 0x01bb,
    // draft
    Onion = 0x01bc,
    // draft
    Onion3 = 0x01bd,
    // I2P base64 (raw public key)
    // draft
    Garlic64 = 0x01be,
    // I2P base32 (hashed public key or encoded public key/checksum+optional secret)
    // draft
    Garlic32 = 0x01bf,
    // draft
    Tls = 0x01c0,
    // Server Name Indication RFC 6066 § 3
    // draft
    Sni = 0x01c1,
    // draft
    Noise = 0x01c6,
    Quic = 0x01cc,
    QuicV1 = 0x01cd,
    // draft
    Webtransport = 0x01d1,
    // TLS certificate's fingerprint as a multihash
    // draft
    Certhash = 0x01d2,
    Ws = 0x01dd,
    Wss = 0x01de,
    P2pWebsocketStar = 0x01df,
    // draft
    Http = 0x01e0,
    // Experimental QUIC over yggdrasil and ironwood routing protocol
    // draft
    Silverpine = 0x3f42,
    // draft
    Plaintextv2 = 0x706c61,
    Unknown,
}
