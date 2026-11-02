// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using NUnit.Framework;

namespace Nethermind.Libp2p.Samples.Chat.Tests;

public class ChatSampleTests
{
    [Test]
    public async Task ChatRejectsPayloadAboveApplicationLimit([Values] bool maui, [Values] bool isListener)
    {
        Channel channel = new();
        ISessionProtocol protocol = maui ? new MauiChat.ChatProtocol() : new global::ChatProtocol();
        Task handler = isListener ? protocol.ListenAsync(channel, null!) : protocol.DialAsync(channel, null!);
        Task<IOResult> write = channel.Reverse.WriteVarintAsync(1024 * 1024 + 1).AsTask();
        try
        {
            Assert.ThrowsAsync<InvalidDataException>(async () => await handler.WaitAsync(TimeSpan.FromSeconds(2)));
            await write.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await channel.AbortAsync();
            await Task.WhenAll(handler, write).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task ChatReceivesLongMessages(
        [Values] bool maui, [Values] bool isListener, [Values(1100, 1024 * 1024 - 1)] int messageLength)
    {
        Channel channel = new();
        TaskCompletionSource<string> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ISessionProtocol protocol = maui
            ? new MauiChat.ChatProtocol { OnServerMessage = message => received.TrySetResult(message) }
            : new global::ChatProtocol { OnServerMessage = message => received.TrySetResult(message) };
        Task handler = isListener ? protocol.ListenAsync(channel, null!) : protocol.DialAsync(channel, null!);
        string message = new('a', messageLength);
        Task<IOResult> write = channel.Reverse.WriteLineAsync(message).AsTask();
        try
        {
            await Task.WhenAny(received.Task, handler).WaitAsync(TimeSpan.FromSeconds(2));
            if (handler.IsCompleted) await handler;
            Assert.That(received.Task.IsCompletedSuccessfully, Is.True);
            Assert.That(await received.Task, Is.EqualTo(message));
            Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(IOResult.Ok));
        }
        finally
        {
            await channel.AbortAsync();
            await Task.WhenAll(handler, write).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
