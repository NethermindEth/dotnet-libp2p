namespace Nethermind.Libp2p.Core.Enums;
public enum Serialization
{
    // Protocol Buffers
    // draft
    Protobuf = 0x50,
    // recursive length prefix
    // draft
    Rlp = 0x60,
    // bencode
    // draft
    Bencode = 0x63,
    // MessagePack
    // draft
    Messagepack = 0x0201,
    // Content Addressable aRchive (CAR)
    // draft
    Car = 0x0202,
    // Signed IPNS Record
    IpnsRecord = 0x0300,
    // CARv2 IndexSorted index format
    // draft
    CarIndexSorted = 0x0400,
    // CARv2 MultihashIndexSorted index format
    // draft
    CarMultihashIndexSorted = 0x0401,
    // SimpleSerialize (SSZ) serialization
    // draft
    Ssz = 0xb501,
    Unknown,
}
