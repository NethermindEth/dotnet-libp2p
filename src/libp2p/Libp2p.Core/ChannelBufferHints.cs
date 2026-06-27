// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public readonly record struct ChannelBufferHints
{
    public ChannelBufferHints(int preferredWriteHeadroom = 0, int preferredWriteTailroom = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preferredWriteHeadroom);
        ArgumentOutOfRangeException.ThrowIfNegative(preferredWriteTailroom);

        PreferredWriteHeadroom = preferredWriteHeadroom;
        PreferredWriteTailroom = preferredWriteTailroom;
    }

    public int PreferredWriteHeadroom { get; }
    public int PreferredWriteTailroom { get; }
}
