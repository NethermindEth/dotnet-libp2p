// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Multiformats.Address.Protocols;

public class Webrtc : MultiaddressProtocol
{
    public Webrtc()
        : base("webrtc", 281, 0)
    {
    }

    public override void Decode(string value)
    {
    }

    public override void Decode(byte[] bytes)
    {
    }

    public override byte[] ToBytes() => EmptyBuffer;
}