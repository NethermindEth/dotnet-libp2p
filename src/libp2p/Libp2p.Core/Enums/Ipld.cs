namespace Nethermind.Libp2p.Core.Enums;
public enum Ipld
{
    // CBOR
    Cbor = 0x51,
    // raw binary
    Raw = 0x55,
    // MerkleDAG protobuf
    DagPb = 0x70,
    // MerkleDAG cbor
    DagCbor = 0x71,
    // Libp2p Public Key
    Libp2pKey = 0x72,
    // Raw Git object
    GitRaw = 0x78,
    // Torrent file info field (bencoded)
    // draft
    TorrentInfo = 0x7b,
    // Torrent file (bencoded)
    // draft
    TorrentFile = 0x7c,
    // Leofcoin Block
    // draft
    LeofcoinBlock = 0x81,
    // Leofcoin Transaction
    // draft
    LeofcoinTx = 0x82,
    // Leofcoin Peer Reputation
    // draft
    LeofcoinPr = 0x83,
    // MerkleDAG JOSE
    // draft
    DagJose = 0x85,
    // MerkleDAG COSE
    // draft
    DagCose = 0x86,
    // Ethereum Header (RLP)
    EthBlock = 0x90,
    // Ethereum Header List (RLP)
    EthBlockList = 0x91,
    // Ethereum Transaction Trie (Eth-Trie)
    EthTxTrie = 0x92,
    // Ethereum Transaction (MarshalBinary)
    EthTx = 0x93,
    // Ethereum Transaction Receipt Trie (Eth-Trie)
    EthTxReceiptTrie = 0x94,
    // Ethereum Transaction Receipt (MarshalBinary)
    EthTxReceipt = 0x95,
    // Ethereum State Trie (Eth-Secure-Trie)
    EthStateTrie = 0x96,
    // Ethereum Account Snapshot (RLP)
    EthAccountSnapshot = 0x97,
    // Ethereum Contract Storage Trie (Eth-Secure-Trie)
    EthStorageTrie = 0x98,
    // Ethereum Transaction Receipt Log Trie (Eth-Trie)
    // draft
    EthReceiptLogTrie = 0x99,
    // Ethereum Transaction Receipt Log (RLP)
    // draft
    EthRecieptLog = 0x9a,
    // Bitcoin Block
    BitcoinBlock = 0xb0,
    // Bitcoin Tx
    BitcoinTx = 0xb1,
    // Bitcoin Witness Commitment
    BitcoinWitnessCommitment = 0xb2,
    // Zcash Block
    ZcashBlock = 0xc0,
    // Zcash Tx
    ZcashTx = 0xc1,
    // Stellar Block
    // draft
    StellarBlock = 0xd0,
    // Stellar Tx
    // draft
    StellarTx = 0xd1,
    // Decred Block
    // draft
    DecredBlock = 0xe0,
    // Decred Tx
    // draft
    DecredTx = 0xe1,
    // Dash Block
    // draft
    DashBlock = 0xf0,
    // Dash Tx
    // draft
    DashTx = 0xf1,
    // Swarm Manifest
    // draft
    SwarmManifest = 0xfa,
    // Swarm Feed
    // draft
    SwarmFeed = 0xfb,
    // Swarm BeeSon
    // draft
    Beeson = 0xfc,
    // MerkleDAG json
    DagJson = 0x0129,
    // SoftWare Heritage persistent IDentifier version 1 snapshot
    // draft
    Swhid1Snp = 0x01f0,
    // JSON (UTF-8-encoded)
    Json = 0x0200,
    // The result of canonicalizing an input according to URDCA-2015 and then expressing its hash value as a multihash value.
    // draft
    Urdca2015Canon = 0xb403,
    // The result of canonicalizing an input according to JCS - JSON Canonicalisation Scheme (RFC 8785)
    // draft
    JsonJcs = 0xb601,
    Unknown,
}
