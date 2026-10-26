// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Extensions;

/// <summary>
/// Extension methods for <see cref="IChannel"/> to convert it into a <see cref="Stream"/>.
/// </summary>
public static class IChannelExtensions
{
    /// <summary>
    /// Converts an <see cref="IChannel"/> to a <see cref="Stream"/>.
    /// </summary>
    /// <param name="channel">The input channel</param>
    /// <returns>The channel as a <see cref="Stream"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="channel"/> is null.</exception>
    public static Stream AsStream(this IChannel channel)
        => AsStream(channel, false);

    /// <summary>
    /// Converts an <see cref="IChannel"/> to a <see cref="Stream"/> with explicit channel ownership.
    /// </summary>
    /// <param name="channel">The input channel.</param>
    /// <param name="leaveOpen">Whether disposing the stream leaves the channel open.</param>
    /// <returns>The channel as a <see cref="Stream"/>.</returns>
    public static Stream AsStream(this IChannel channel, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return new ChannelStream(channel, leaveOpen);
    }
}
