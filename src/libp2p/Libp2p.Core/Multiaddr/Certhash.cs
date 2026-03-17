// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Base;

namespace Multiformats.Address.Protocols;

public class Certhash : MultiaddressProtocol
{
    public Certhash()
        : base("certhash", 466, -1)
    {
    }

    public Certhash(string value)
        : this()
    {
        Decode(value);
    }

    public byte[] Hash => Value as byte[] ?? [];

    public override void Decode(string value)
    {
        Value = Multibase.Decode(value, out MultibaseEncoding _);
    }

    public override void Decode(byte[] bytes)
    {
        Value = bytes;
    }

    public override byte[] ToBytes() => Hash;

    public override string ToString() => Multibase.Encode(MultibaseEncoding.Base64Url, Hash);
}