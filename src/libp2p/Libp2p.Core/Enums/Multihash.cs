namespace Nethermind.Libp2p.Core.Enums;

public enum Multihash
{
    // raw binary
    Identity = 0x00,
    Sha1 = 0x11,
    Sha2256 = 0x12,
    Sha2512 = 0x13,
    Sha3512 = 0x14,
    Sha3384 = 0x15,
    Sha3256 = 0x16,
    Sha3224 = 0x17,
    // draft
    Shake128 = 0x18,
    // draft
    Shake256 = 0x19,
    // keccak has variable output length. The number specifies the core length
    // draft
    Keccak224 = 0x1a,
    // draft
    Keccak256 = 0x1b,
    // draft
    Keccak384 = 0x1c,
    // draft
    Keccak512 = 0x1d,
    // BLAKE3 has a default 32 byte output length. The maximum length is (2^64)-1 bytes.
    // draft
    Blake3 = 0x1e,
    // aka SHA-384; as specified by FIPS 180-4.
    Sha2384 = 0x20,
    // draft
    DblSha2256 = 0x56,
    // draft
    Md4 = 0xd4,
    // draft
    Md5 = 0xd5,
    // SHA2-256 with the two most significant bits from the last byte zeroed (as via a mask with 0b00111111) - used for proving trees as in Filecoin
    Sha2256Trunc254Padded = 0x1012,
    // aka SHA-224; as specified by FIPS 180-4.
    Sha2224 = 0x1013,
    // aka SHA-512/224; as specified by FIPS 180-4.
    Sha2512224 = 0x1014,
    // aka SHA-512/256; as specified by FIPS 180-4.
    Sha2512256 = 0x1015,
    // draft
    Ripemd128 = 0x1052,
    // draft
    Ripemd160 = 0x1053,
    // draft
    Ripemd256 = 0x1054,
    // draft
    Ripemd320 = 0x1055,
    // draft
    X11 = 0x1100,
    // KangarooTwelve is an extendable-output hash function based on Keccak-p
    // draft
    Kangarootwelve = 0x1d01,
    // draft
    Sm3256 = 0x534d,
    // Blake2b consists of 64 output lengths that give different hashes
    // draft
    Blake2b8 = 0xb201,
    // draft
    Blake2b16 = 0xb202,
    // draft
    Blake2b24 = 0xb203,
    // draft
    Blake2b32 = 0xb204,
    // draft
    Blake2b40 = 0xb205,
    // draft
    Blake2b48 = 0xb206,
    // draft
    Blake2b56 = 0xb207,
    // draft
    Blake2b64 = 0xb208,
    // draft
    Blake2b72 = 0xb209,
    // draft
    Blake2b80 = 0xb20a,
    // draft
    Blake2b88 = 0xb20b,
    // draft
    Blake2b96 = 0xb20c,
    // draft
    Blake2b104 = 0xb20d,
    // draft
    Blake2b112 = 0xb20e,
    // draft
    Blake2b120 = 0xb20f,
    // draft
    Blake2b128 = 0xb210,
    // draft
    Blake2b136 = 0xb211,
    // draft
    Blake2b144 = 0xb212,
    // draft
    Blake2b152 = 0xb213,
    // draft
    Blake2b160 = 0xb214,
    // draft
    Blake2b168 = 0xb215,
    // draft
    Blake2b176 = 0xb216,
    // draft
    Blake2b184 = 0xb217,
    // draft
    Blake2b192 = 0xb218,
    // draft
    Blake2b200 = 0xb219,
    // draft
    Blake2b208 = 0xb21a,
    // draft
    Blake2b216 = 0xb21b,
    // draft
    Blake2b224 = 0xb21c,
    // draft
    Blake2b232 = 0xb21d,
    // draft
    Blake2b240 = 0xb21e,
    // draft
    Blake2b248 = 0xb21f,
    Blake2b256 = 0xb220,
    // draft
    Blake2b264 = 0xb221,
    // draft
    Blake2b272 = 0xb222,
    // draft
    Blake2b280 = 0xb223,
    // draft
    Blake2b288 = 0xb224,
    // draft
    Blake2b296 = 0xb225,
    // draft
    Blake2b304 = 0xb226,
    // draft
    Blake2b312 = 0xb227,
    // draft
    Blake2b320 = 0xb228,
    // draft
    Blake2b328 = 0xb229,
    // draft
    Blake2b336 = 0xb22a,
    // draft
    Blake2b344 = 0xb22b,
    // draft
    Blake2b352 = 0xb22c,
    // draft
    Blake2b360 = 0xb22d,
    // draft
    Blake2b368 = 0xb22e,
    // draft
    Blake2b376 = 0xb22f,
    // draft
    Blake2b384 = 0xb230,
    // draft
    Blake2b392 = 0xb231,
    // draft
    Blake2b400 = 0xb232,
    // draft
    Blake2b408 = 0xb233,
    // draft
    Blake2b416 = 0xb234,
    // draft
    Blake2b424 = 0xb235,
    // draft
    Blake2b432 = 0xb236,
    // draft
    Blake2b440 = 0xb237,
    // draft
    Blake2b448 = 0xb238,
    // draft
    Blake2b456 = 0xb239,
    // draft
    Blake2b464 = 0xb23a,
    // draft
    Blake2b472 = 0xb23b,
    // draft
    Blake2b480 = 0xb23c,
    // draft
    Blake2b488 = 0xb23d,
    // draft
    Blake2b496 = 0xb23e,
    // draft
    Blake2b504 = 0xb23f,
    // draft
    Blake2b512 = 0xb240,
    // Blake2s consists of 32 output lengths that give different hashes
    // draft
    Blake2s8 = 0xb241,
    // draft
    Blake2s16 = 0xb242,
    // draft
    Blake2s24 = 0xb243,
    // draft
    Blake2s32 = 0xb244,
    // draft
    Blake2s40 = 0xb245,
    // draft
    Blake2s48 = 0xb246,
    // draft
    Blake2s56 = 0xb247,
    // draft
    Blake2s64 = 0xb248,
    // draft
    Blake2s72 = 0xb249,
    // draft
    Blake2s80 = 0xb24a,
    // draft
    Blake2s88 = 0xb24b,
    // draft
    Blake2s96 = 0xb24c,
    // draft
    Blake2s104 = 0xb24d,
    // draft
    Blake2s112 = 0xb24e,
    // draft
    Blake2s120 = 0xb24f,
    // draft
    Blake2s128 = 0xb250,
    // draft
    Blake2s136 = 0xb251,
    // draft
    Blake2s144 = 0xb252,
    // draft
    Blake2s152 = 0xb253,
    // draft
    Blake2s160 = 0xb254,
    // draft
    Blake2s168 = 0xb255,
    // draft
    Blake2s176 = 0xb256,
    // draft
    Blake2s184 = 0xb257,
    // draft
    Blake2s192 = 0xb258,
    // draft
    Blake2s200 = 0xb259,
    // draft
    Blake2s208 = 0xb25a,
    // draft
    Blake2s216 = 0xb25b,
    // draft
    Blake2s224 = 0xb25c,
    // draft
    Blake2s232 = 0xb25d,
    // draft
    Blake2s240 = 0xb25e,
    // draft
    Blake2s248 = 0xb25f,
    // draft
    Blake2s256 = 0xb260,
    // Skein256 consists of 32 output lengths that give different hashes
    // draft
    Skein2568 = 0xb301,
    // draft
    Skein25616 = 0xb302,
    // draft
    Skein25624 = 0xb303,
    // draft
    Skein25632 = 0xb304,
    // draft
    Skein25640 = 0xb305,
    // draft
    Skein25648 = 0xb306,
    // draft
    Skein25656 = 0xb307,
    // draft
    Skein25664 = 0xb308,
    // draft
    Skein25672 = 0xb309,
    // draft
    Skein25680 = 0xb30a,
    // draft
    Skein25688 = 0xb30b,
    // draft
    Skein25696 = 0xb30c,
    // draft
    Skein256104 = 0xb30d,
    // draft
    Skein256112 = 0xb30e,
    // draft
    Skein256120 = 0xb30f,
    // draft
    Skein256128 = 0xb310,
    // draft
    Skein256136 = 0xb311,
    // draft
    Skein256144 = 0xb312,
    // draft
    Skein256152 = 0xb313,
    // draft
    Skein256160 = 0xb314,
    // draft
    Skein256168 = 0xb315,
    // draft
    Skein256176 = 0xb316,
    // draft
    Skein256184 = 0xb317,
    // draft
    Skein256192 = 0xb318,
    // draft
    Skein256200 = 0xb319,
    // draft
    Skein256208 = 0xb31a,
    // draft
    Skein256216 = 0xb31b,
    // draft
    Skein256224 = 0xb31c,
    // draft
    Skein256232 = 0xb31d,
    // draft
    Skein256240 = 0xb31e,
    // draft
    Skein256248 = 0xb31f,
    // draft
    Skein256256 = 0xb320,
    // Skein512 consists of 64 output lengths that give different hashes
    // draft
    Skein5128 = 0xb321,
    // draft
    Skein51216 = 0xb322,
    // draft
    Skein51224 = 0xb323,
    // draft
    Skein51232 = 0xb324,
    // draft
    Skein51240 = 0xb325,
    // draft
    Skein51248 = 0xb326,
    // draft
    Skein51256 = 0xb327,
    // draft
    Skein51264 = 0xb328,
    // draft
    Skein51272 = 0xb329,
    // draft
    Skein51280 = 0xb32a,
    // draft
    Skein51288 = 0xb32b,
    // draft
    Skein51296 = 0xb32c,
    // draft
    Skein512104 = 0xb32d,
    // draft
    Skein512112 = 0xb32e,
    // draft
    Skein512120 = 0xb32f,
    // draft
    Skein512128 = 0xb330,
    // draft
    Skein512136 = 0xb331,
    // draft
    Skein512144 = 0xb332,
    // draft
    Skein512152 = 0xb333,
    // draft
    Skein512160 = 0xb334,
    // draft
    Skein512168 = 0xb335,
    // draft
    Skein512176 = 0xb336,
    // draft
    Skein512184 = 0xb337,
    // draft
    Skein512192 = 0xb338,
    // draft
    Skein512200 = 0xb339,
    // draft
    Skein512208 = 0xb33a,
    // draft
    Skein512216 = 0xb33b,
    // draft
    Skein512224 = 0xb33c,
    // draft
    Skein512232 = 0xb33d,
    // draft
    Skein512240 = 0xb33e,
    // draft
    Skein512248 = 0xb33f,
    // draft
    Skein512256 = 0xb340,
    // draft
    Skein512264 = 0xb341,
    // draft
    Skein512272 = 0xb342,
    // draft
    Skein512280 = 0xb343,
    // draft
    Skein512288 = 0xb344,
    // draft
    Skein512296 = 0xb345,
    // draft
    Skein512304 = 0xb346,
    // draft
    Skein512312 = 0xb347,
    // draft
    Skein512320 = 0xb348,
    // draft
    Skein512328 = 0xb349,
    // draft
    Skein512336 = 0xb34a,
    // draft
    Skein512344 = 0xb34b,
    // draft
    Skein512352 = 0xb34c,
    // draft
    Skein512360 = 0xb34d,
    // draft
    Skein512368 = 0xb34e,
    // draft
    Skein512376 = 0xb34f,
    // draft
    Skein512384 = 0xb350,
    // draft
    Skein512392 = 0xb351,
    // draft
    Skein512400 = 0xb352,
    // draft
    Skein512408 = 0xb353,
    // draft
    Skein512416 = 0xb354,
    // draft
    Skein512424 = 0xb355,
    // draft
    Skein512432 = 0xb356,
    // draft
    Skein512440 = 0xb357,
    // draft
    Skein512448 = 0xb358,
    // draft
    Skein512456 = 0xb359,
    // draft
    Skein512464 = 0xb35a,
    // draft
    Skein512472 = 0xb35b,
    // draft
    Skein512480 = 0xb35c,
    // draft
    Skein512488 = 0xb35d,
    // draft
    Skein512496 = 0xb35e,
    // draft
    Skein512504 = 0xb35f,
    // draft
    Skein512512 = 0xb360,
    // Skein1024 consists of 128 output lengths that give different hashes
    // draft
    Skein10248 = 0xb361,
    // draft
    Skein102416 = 0xb362,
    // draft
    Skein102424 = 0xb363,
    // draft
    Skein102432 = 0xb364,
    // draft
    Skein102440 = 0xb365,
    // draft
    Skein102448 = 0xb366,
    // draft
    Skein102456 = 0xb367,
    // draft
    Skein102464 = 0xb368,
    // draft
    Skein102472 = 0xb369,
    // draft
    Skein102480 = 0xb36a,
    // draft
    Skein102488 = 0xb36b,
    // draft
    Skein102496 = 0xb36c,
    // draft
    Skein1024104 = 0xb36d,
    // draft
    Skein1024112 = 0xb36e,
    // draft
    Skein1024120 = 0xb36f,
    // draft
    Skein1024128 = 0xb370,
    // draft
    Skein1024136 = 0xb371,
    // draft
    Skein1024144 = 0xb372,
    // draft
    Skein1024152 = 0xb373,
    // draft
    Skein1024160 = 0xb374,
    // draft
    Skein1024168 = 0xb375,
    // draft
    Skein1024176 = 0xb376,
    // draft
    Skein1024184 = 0xb377,
    // draft
    Skein1024192 = 0xb378,
    // draft
    Skein1024200 = 0xb379,
    // draft
    Skein1024208 = 0xb37a,
    // draft
    Skein1024216 = 0xb37b,
    // draft
    Skein1024224 = 0xb37c,
    // draft
    Skein1024232 = 0xb37d,
    // draft
    Skein1024240 = 0xb37e,
    // draft
    Skein1024248 = 0xb37f,
    // draft
    Skein1024256 = 0xb380,
    // draft
    Skein1024264 = 0xb381,
    // draft
    Skein1024272 = 0xb382,
    // draft
    Skein1024280 = 0xb383,
    // draft
    Skein1024288 = 0xb384,
    // draft
    Skein1024296 = 0xb385,
    // draft
    Skein1024304 = 0xb386,
    // draft
    Skein1024312 = 0xb387,
    // draft
    Skein1024320 = 0xb388,
    // draft
    Skein1024328 = 0xb389,
    // draft
    Skein1024336 = 0xb38a,
    // draft
    Skein1024344 = 0xb38b,
    // draft
    Skein1024352 = 0xb38c,
    // draft
    Skein1024360 = 0xb38d,
    // draft
    Skein1024368 = 0xb38e,
    // draft
    Skein1024376 = 0xb38f,
    // draft
    Skein1024384 = 0xb390,
    // draft
    Skein1024392 = 0xb391,
    // draft
    Skein1024400 = 0xb392,
    // draft
    Skein1024408 = 0xb393,
    // draft
    Skein1024416 = 0xb394,
    // draft
    Skein1024424 = 0xb395,
    // draft
    Skein1024432 = 0xb396,
    // draft
    Skein1024440 = 0xb397,
    // draft
    Skein1024448 = 0xb398,
    // draft
    Skein1024456 = 0xb399,
    // draft
    Skein1024464 = 0xb39a,
    // draft
    Skein1024472 = 0xb39b,
    // draft
    Skein1024480 = 0xb39c,
    // draft
    Skein1024488 = 0xb39d,
    // draft
    Skein1024496 = 0xb39e,
    // draft
    Skein1024504 = 0xb39f,
    // draft
    Skein1024512 = 0xb3a0,
    // draft
    Skein1024520 = 0xb3a1,
    // draft
    Skein1024528 = 0xb3a2,
    // draft
    Skein1024536 = 0xb3a3,
    // draft
    Skein1024544 = 0xb3a4,
    // draft
    Skein1024552 = 0xb3a5,
    // draft
    Skein1024560 = 0xb3a6,
    // draft
    Skein1024568 = 0xb3a7,
    // draft
    Skein1024576 = 0xb3a8,
    // draft
    Skein1024584 = 0xb3a9,
    // draft
    Skein1024592 = 0xb3aa,
    // draft
    Skein1024600 = 0xb3ab,
    // draft
    Skein1024608 = 0xb3ac,
    // draft
    Skein1024616 = 0xb3ad,
    // draft
    Skein1024624 = 0xb3ae,
    // draft
    Skein1024632 = 0xb3af,
    // draft
    Skein1024640 = 0xb3b0,
    // draft
    Skein1024648 = 0xb3b1,
    // draft
    Skein1024656 = 0xb3b2,
    // draft
    Skein1024664 = 0xb3b3,
    // draft
    Skein1024672 = 0xb3b4,
    // draft
    Skein1024680 = 0xb3b5,
    // draft
    Skein1024688 = 0xb3b6,
    // draft
    Skein1024696 = 0xb3b7,
    // draft
    Skein1024704 = 0xb3b8,
    // draft
    Skein1024712 = 0xb3b9,
    // draft
    Skein1024720 = 0xb3ba,
    // draft
    Skein1024728 = 0xb3bb,
    // draft
    Skein1024736 = 0xb3bc,
    // draft
    Skein1024744 = 0xb3bd,
    // draft
    Skein1024752 = 0xb3be,
    // draft
    Skein1024760 = 0xb3bf,
    // draft
    Skein1024768 = 0xb3c0,
    // draft
    Skein1024776 = 0xb3c1,
    // draft
    Skein1024784 = 0xb3c2,
    // draft
    Skein1024792 = 0xb3c3,
    // draft
    Skein1024800 = 0xb3c4,
    // draft
    Skein1024808 = 0xb3c5,
    // draft
    Skein1024816 = 0xb3c6,
    // draft
    Skein1024824 = 0xb3c7,
    // draft
    Skein1024832 = 0xb3c8,
    // draft
    Skein1024840 = 0xb3c9,
    // draft
    Skein1024848 = 0xb3ca,
    // draft
    Skein1024856 = 0xb3cb,
    // draft
    Skein1024864 = 0xb3cc,
    // draft
    Skein1024872 = 0xb3cd,
    // draft
    Skein1024880 = 0xb3ce,
    // draft
    Skein1024888 = 0xb3cf,
    // draft
    Skein1024896 = 0xb3d0,
    // draft
    Skein1024904 = 0xb3d1,
    // draft
    Skein1024912 = 0xb3d2,
    // draft
    Skein1024920 = 0xb3d3,
    // draft
    Skein1024928 = 0xb3d4,
    // draft
    Skein1024936 = 0xb3d5,
    // draft
    Skein1024944 = 0xb3d6,
    // draft
    Skein1024952 = 0xb3d7,
    // draft
    Skein1024960 = 0xb3d8,
    // draft
    Skein1024968 = 0xb3d9,
    // draft
    Skein1024976 = 0xb3da,
    // draft
    Skein1024984 = 0xb3db,
    // draft
    Skein1024992 = 0xb3dc,
    // draft
    Skein10241000 = 0xb3dd,
    // draft
    Skein10241008 = 0xb3de,
    // draft
    Skein10241016 = 0xb3df,
    // draft
    Skein10241024 = 0xb3e0,
    // Poseidon using BLS12-381 and arity of 2 with Filecoin parameters
    PoseidonBls12_381A2Fc1 = 0xb401,
    // Poseidon using BLS12-381 and arity of 2 with Filecoin parameters - high-security variant
    // draft
    PoseidonBls12_381A2Fc1Sc = 0xb402,
    // SSZ Merkle tree root using SHA2-256 as the hashing function and SSZ serialization for the block binary
    // draft
    SszSha2256Bmt = 0xb502,
    Unknown,
}
