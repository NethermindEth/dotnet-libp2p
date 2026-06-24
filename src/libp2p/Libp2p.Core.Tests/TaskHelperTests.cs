// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Extensions;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

internal class TaskHelperTests
{

    [Test]
    public async Task Test_AllExceptions_RaiseAggregateException()
    {
        TaskCompletionSource tcs1 = new();
        TaskCompletionSource tcs2 = new();
        TaskCompletionSource<bool> tcs3 = new();

        Task<Task> t = TaskHelper.FirstSuccess(tcs1.Task, tcs2.Task, tcs3.Task);

        tcs1.SetException(new Exception());
        tcs2.SetException(new Exception());
        tcs3.SetException(new Exception());

        await t.ContinueWith((t) =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.IsFaulted, Is.True);
                Assert.That(t.Exception?.InnerException, Is.TypeOf<AggregateException>());
                Assert.That((t.Exception?.InnerException as AggregateException)?.InnerExceptions, Has.Count.EqualTo(3));
            });
        });
    }

    [Test]
    public async Task Test_SingleSuccess_ReturnsCompletedTask()
    {
        TaskCompletionSource tcs1 = new();
        TaskCompletionSource tcs2 = new();
        TaskCompletionSource<bool> tcs3 = new();

        Task<Task> t = TaskHelper.FirstSuccess(tcs1.Task, tcs2.Task, tcs3.Task);

        tcs1.SetException(new Exception());
        tcs2.SetException(new Exception());
        _ = Task.Delay(100).ContinueWith(t => tcs3.SetResult(true));

        Task result = await t;

        Assert.That(result, Is.EqualTo(tcs3.Task));
        Assert.That((result as Task<bool>)!.Result, Is.EqualTo(true));
    }

    [Test]
    public async Task Test_SessionExistsException_ReturnsFaultedTask()
    {
        TaskCompletionSource tcs1 = new();
        TaskCompletionSource tcs2 = new();

        Task<Task> t = TaskHelper.FirstSuccess(tcs1.Task, tcs2.Task);
        SessionExistsException exception = new(TestPeers.Identity(1).PeerId);

        tcs1.SetException(exception);
        tcs2.SetException(new Exception());

        Task result = await t;

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(tcs1.Task));
            Assert.That(result.Exception?.InnerException, Is.SameAs(exception));
        });
    }
}
