// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT
// SPDX-Author: Luca Fabbri

using Nethermind.Libp2p.Core.Extensions;

namespace Nethermind.Libp2p.Core.Tests;

public class ChannelTests
{
    /// <summary>
    /// Tests the i channel extensions as stream write read asynchronous.
    /// </summary>
    [Test]
    public async Task Test_IChannelExtensions_AsStreamWriteReadAsync()
    {
        // Arrange
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using Stream reverseStream = channel.Reverse.AsStream();

        byte[] data = [1, 2, 3, 4];

        // Act
        var _ = Task.Run(async () => await stream.WriteAsync(data, 0, data.Length));

        byte[] buffer = new byte[4];
        int bytesRead = await reverseStream.ReadAsync(buffer, 0, buffer.Length);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(data.Length));
        Assert.That(buffer, Is.EquivalentTo(data));
    }

    [Test]
    public async Task Test_IChannelExtensions_AsStreamWriteReadAsync_Empty()
    {
        // Arrange
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using Stream reverseStream = channel.Reverse.AsStream();
        byte[] data = Array.Empty<byte>();

        // Act
        var _ = Task.Run(async () => await stream.WriteAsync(data, 0, data.Length));
        byte[] buffer = new byte[4];
        int bytesRead = await reverseStream.ReadAsync(buffer, 0, buffer.Length);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(data.Length));
        Assert.That(buffer, Is.EquivalentTo(new byte[4])); // Buffer should remain unchanged
    }

    [Test]
    public async Task Test_IChannelExtensions_AsStreamWriteReadAsync_ZeroLength()
    {
        // Arrange
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using Stream reverseStream = channel.Reverse.AsStream();
        byte[] data = [1, 2, 3, 4];

        // Act
        var _ = Task.Run(async () => await stream.WriteAsync(data, 0, 0)); // Writing zero length
        byte[] buffer = new byte[4];
        int bytesRead = await reverseStream.ReadAsync(buffer, 0, buffer.Length);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(0));
        Assert.That(buffer, Is.EquivalentTo(new byte[4])); // Buffer should remain unchanged
    }
}
