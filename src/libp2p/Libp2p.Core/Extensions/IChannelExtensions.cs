// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT
// SPDX-Author: Luca Fabbri

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
  /// <returns>The Channel as Stream</returns>
  /// <exception cref="ArgumentNullException">Channel is null throws an <see cref="ArgumentNullException"/></exception>
  public static Stream AsStream(this IChannel channel)
  {
    if (channel is null)
    {
      throw new ArgumentNullException(nameof(channel));
    }

    return new ChannelStream(channel);
  }
}
