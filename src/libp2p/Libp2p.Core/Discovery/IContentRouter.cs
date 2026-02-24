// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Discovery;

/// <summary>
/// Interface for content routing in the libp2p network.
/// </summary>
public interface IContentRouter
{
    /// <summary>
    /// Provides the specified content to the network.
    /// </summary>
    /// <param name="contentId">The content identifier.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    ValueTask ProvideAsync(byte[] contentId);

    /// <summary>
    /// Finds providers for the specified content.
    /// </summary>
    /// <param name="contentId">The content identifier.</param>
    /// <param name="limit">The maximum number of providers to return.</param>
    /// <returns>A collection of peer IDs that provide the content.</returns>
    ValueTask<IEnumerable<PeerId>> FindProvidersAsync(byte[] contentId, int limit = 20);

    /// <summary>
    /// Puts a value in the DHT.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    ValueTask PutValueAsync(byte[] key, byte[] value);

    /// <summary>
    /// Gets a value from the DHT.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null if not found.</returns>
    ValueTask<byte[]?> GetValueAsync(byte[] key);
}
