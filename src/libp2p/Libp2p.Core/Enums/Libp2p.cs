namespace Nethermind.Libp2p.Core.Enums;
public enum Libp2p
{
    // libp2p peer record type
    Libp2pPeerRecord = 0x0301,
    // libp2p relay reservation voucher
    Libp2pRelayRsvp = 0x0302,
    // in memory transport for self-dialing and testing; arbitrary
    Memorytransport = 0x0309,
    Unknown,
}
