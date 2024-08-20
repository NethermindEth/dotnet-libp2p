// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Extensions;

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


        Task r = await t;
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
    }
}
