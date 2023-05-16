// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Dto;
using Multiformats.Hash;
using SimpleBase;

namespace Nethermind.Libp2p.Core;

public class RawPeerId
{
    private readonly byte[] _peerId;

    public RawPeerId(PublicKey publicKey)
    {
        _peerId = publicKey.ToByteArray();
    }

    public override string ToString()
    {
        return Base58.Bitcoin.Encode(ToByteArray());
    }

    public byte[] ToByteArray()
    {
        return Multihash.Encode(_peerId, HashType.ID);
    }
}
