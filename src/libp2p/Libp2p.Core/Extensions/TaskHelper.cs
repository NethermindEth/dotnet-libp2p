// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Libp2p.Core.Extensions;

internal static class TaskHelper
{
    public static async Task<Task> FirstSuccess(params Task[] tasks)
    {
        TaskCompletionSource<Task> tcs = new();

        Task all = Task.WhenAll(tasks.Select(t => t.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                tcs.TrySetResult(t);
            }
            if (t.IsFaulted && t.Exception.InnerException is SessionExistsException)
            {
                tcs.TrySetResult(t);
            }
        })));

        Task result = await Task.WhenAny(tcs.Task, all);
        if (result == all)
        {
            throw new AggregateException(tasks.Select(t => t.Exception).Where(ex => ex is not null)!);
        }
        return tcs.Task.Result;
    }
}
