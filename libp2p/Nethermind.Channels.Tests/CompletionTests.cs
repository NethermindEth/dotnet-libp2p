// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Reflection;
using NUnit.Framework;

namespace Nethermind.Channels.Tests;

public class CompletionTests
{
    [Test]
    public async Task CompletionCallbacksDoNotRunUnderTheDataLock()
    {
        Channel channel = new();
        FieldInfo dataLock = typeof(Channel.ReaderWriter).GetField("_dataLock", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Lock readLock = (Lock)dataLock.GetValue(channel.Reader)!;
        Lock writeLock = (Lock)dataLock.GetValue(channel.Writer)!;
        TaskCompletionSource<bool> callbackHeldLock = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Register and complete without a synchronization context so an inline continuation
        // would execute on the second EOF's thread while it holds the channel's data lock.
        await Task.Run(async () =>
        {
            channel.GetAwaiter().OnCompleted(() =>
                callbackHeldLock.TrySetResult(readLock.IsHeldByCurrentThread || writeLock.IsHeldByCurrentThread));
            Assert.That(await channel.WriteEofAsync(), Is.EqualTo(IOResult.Ok));
            Assert.That(await channel.Reverse.WriteEofAsync(), Is.EqualTo(IOResult.Ok));
        });

        Assert.That(await callbackHeldLock.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.False,
            "Completion callbacks may close other channels; invoking them under the data lock can invert lock order.");
    }
}
