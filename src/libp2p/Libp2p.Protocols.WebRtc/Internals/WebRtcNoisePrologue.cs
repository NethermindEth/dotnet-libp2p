// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Text;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal static class WebRtcNoisePrologue
{
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("libp2p-webrtc-noise:");

    public static byte[] Build(DtlsFingerprint dialerFingerprint, DtlsFingerprint listenerFingerprint)
        => [.. Prefix, .. dialerFingerprint.ToMultihashBytes(), .. listenerFingerprint.ToMultihashBytes()];
}