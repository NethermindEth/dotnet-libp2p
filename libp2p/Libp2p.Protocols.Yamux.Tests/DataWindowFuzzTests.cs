// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.Yamux;

namespace Nethermind.Libp2p.Protocols.Yamux.Tests;

/// <summary>
/// Deterministic property fuzzer for the flow-control windows.
/// <see cref="LocalDataWindow.TrySpend"/> / <see cref="LocalDataWindow.ExtendIfNeeded"/> /
/// <see cref="LocalDataWindow.RecordConsumed"/> and
/// <see cref="RemoteDataWindow.TryExtend"/> / <see cref="RemoteDataWindow.SpendOrWait"/>
/// must never go negative or overflow, the local window must stay within
/// <c>[0, MaxWindowSize]</c>, and concurrent spend/extend must not deadlock.
/// Committed seeds make every run reproducible; no new dependencies.
/// </summary>
[TestFixture]
public class DataWindowFuzzTests
{
    public static IEnumerable<int> Seeds()
    {
        for (int seed = 2001; seed <= 2010; seed++)
        {
            yield return seed;
        }
    }

    [TestCaseSource(nameof(Seeds))]
    public void LocalWindow_RandomOps_StayWithinBounds(int seed)
    {
        Random rng = new(seed);
        foreach (bool dynamic in new[] { false, true })
        {
            const int initial = 1000;
            const int max = 4000;
            LocalDataWindow window = new(new YamuxWindowSettings
            {
                InitialWindowSize = initial,
                MaxWindowSize = max,
                UseDynamicWindow = dynamic
            });
            for (int i = 0; i < 2000; i++)
            {
                switch (rng.Next(3))
                {
                    case 0:
                        window.TrySpend(PickSpend(rng));
                        break;
                    case 1:
                        window.RecordConsumed(PickSpend(rng));
                        break;
                    default:
                        Assert.That(window.ExtendIfNeeded(), Is.GreaterThanOrEqualTo(0));
                        break;
                }
                Assert.That(window.Available, Is.GreaterThanOrEqualTo(0),
                    $"seed {seed} dynamic={dynamic} iter {i}: window went negative");
                Assert.That(window.Available, Is.LessThanOrEqualTo(max),
                    $"seed {seed} dynamic={dynamic} iter {i}: window exceeded max");
            }
        }
    }

    [Test]
    public void LocalWindow_FailedSpend_NeverGoesNegative(
        [Values(1, 999, 1000)] int spendFirst)
    {
        LocalDataWindow window = new(new YamuxWindowSettings
        {
            InitialWindowSize = 1000,
            MaxWindowSize = 4000,
            UseDynamicWindow = false
        });
        Assert.That(window.TrySpend(spendFirst), Is.True);
        int remaining = 1000 - spendFirst;
        Assert.That(window.TrySpend(remaining + 1), Is.False, "Overspend must fail.");
        Assert.That(window.Available, Is.GreaterThanOrEqualTo(0),
            "A failed spend must not drive the window negative.");
        Assert.That(window.Available, Is.EqualTo(remaining),
            "A failed spend must leave the available credit untouched.");
    }

    [Test]
    public void LocalWindow_NegativeSpend_IsRejectedWithoutSideEffects(
        [Values(-1, -100, int.MinValue)] int badSpend)
    {
        LocalDataWindow window = new(new YamuxWindowSettings
        {
            InitialWindowSize = 1000,
            MaxWindowSize = 4000,
            UseDynamicWindow = false
        });
        Assert.That(window.TrySpend(badSpend), Is.False, "Negative spend must fail instead of granting credit.");
        Assert.That(window.Available, Is.EqualTo(1000));
        Assert.That(window.TrySpend(0), Is.True, "Zero spend stays a no-op success.");
        Assert.That(window.Available, Is.EqualTo(1000));
    }

    [Test]
    public async Task LocalWindow_ConcurrentSpendAndExtend_NoDeadlockAndBounded()
    {
        LocalDataWindow window = new(new YamuxWindowSettings
        {
            InitialWindowSize = 262144,
            MaxWindowSize = 4 * 1024 * 1024,
            UseDynamicWindow = true
        });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        Task[] workers = new Task[8];
        for (int w = 0; w < workers.Length; w++)
        {
            int worker = w;
            workers[w] = Task.Run(() =>
            {
                Random rng = new(3000 + worker);
                for (int i = 0; i < 5000; i++)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    window.TrySpend(rng.Next(0, 4096));
                    window.RecordConsumed(rng.Next(0, 4096));
                    if (i % 4 == 0)
                    {
                        window.ExtendIfNeeded();
                    }
                    Assert.That(window.Available, Is.GreaterThanOrEqualTo(0));
                    Assert.That(window.Available, Is.LessThanOrEqualTo(4 * 1024 * 1024));
                }
            });
        }
        // A deadlock would surface here as a timeout instead of a hung suite.
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(20));
    }

    [TestCaseSource(nameof(Seeds))]
    public async Task RemoteWindow_RandomOps_NeverNegativeOrOverflow(int seed)
    {
        Random rng = new(seed);
        RemoteDataWindow window = new(defaultWindowSize: 1000);
        for (int i = 0; i < 500; i++)
        {
            if (rng.Next(2) == 0)
            {
                int length = PickExtendLength(rng);
                int before = window.Available;
                bool ok = window.TryExtend(length, out int after);
                Assert.That(window.Available, Is.GreaterThanOrEqualTo(0));
                if (!ok)
                {
                    Assert.That(after, Is.EqualTo(before), "Rejected extension must not lose credit.");
                    Assert.That(window.Available, Is.EqualTo(before));
                }
                else
                {
                    Assert.That(after, Is.EqualTo(before + length));
                    Assert.That(window.Available, Is.EqualTo(after));
                }
            }
            else
            {
                using CancellationTokenSource opTimeout = new(TimeSpan.FromSeconds(2));
                try
                {
                    int requested = rng.Next(1, 2000);
                    int spent = await window.SpendOrWait(requested, opTimeout.Token);
                    Assert.That(spent, Is.InRange(1, requested));
                    Assert.That(window.Available, Is.GreaterThanOrEqualTo(0));
                }
                catch (OperationCanceledException)
                {
                    // No credit available within the budget: must cancel, never hang.
                    Assert.That(window.Available, Is.EqualTo(0));
                }
            }
        }
    }

    [Test]
    public void RemoteWindow_OverflowExtension_RejectedWithoutLosingCredit(
        [Values(int.MaxValue, int.MaxValue - 262143)] int delta)
    {
        RemoteDataWindow window = new();
        int before = window.Available;
        Assert.That(window.TryExtend(delta, out int available), Is.False);
        Assert.That(available, Is.EqualTo(before));
        Assert.That(window.Available, Is.EqualTo(before));
    }

    [Test]
    public void RemoteWindow_NegativeExtension_RejectedWithoutSideEffects(
        [Values(-1, -1000, int.MinValue)] int delta)
    {
        RemoteDataWindow window = new();
        int before = window.Available;
        Assert.That(window.TryExtend(delta, out int available), Is.False);
        Assert.That(available, Is.EqualTo(before));
        Assert.That(window.Available, Is.EqualTo(before));
    }

    [Test]
    public void RemoteWindow_NonPositiveSpend_Throws(
        [Values(0, -1, int.MinValue)] int requested)
    {
        RemoteDataWindow window = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => window.SpendOrWait(requested).GetAwaiter().GetResult());
    }

    [Test]
    public async Task RemoteWindow_ConcurrentSpendAndExtend_NoDeadlock()
    {
        RemoteDataWindow window = new(defaultWindowSize: 0);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        Task spender = Task.Run(async () =>
        {
            int remaining = 200000;
            while (remaining > 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                remaining -= await window.SpendOrWait(remaining, timeout.Token);
            }
        });
        Task extender = Task.Run(() =>
        {
            Random rng = new(4000);
            int granted = 0;
            while (granted < 200000)
            {
                timeout.Token.ThrowIfCancellationRequested();
                int chunk = rng.Next(1, 4096);
                if (window.TryExtend(chunk, out _))
                {
                    granted += chunk;
                }
            }
        });
        // A lost wake-up or deadlock would surface here as a timeout instead of a hung suite.
        await Task.WhenAll(spender, extender).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(window.Available, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task RemoteWindow_MultipleSpenders_NoLostCreditNoDeadlock()
    {
        // Several spenders contending on small credit force compare-exchange
        // retries and waiter/signal races; the credit accounting must stay exact.
        // Work is pre-partitioned so no spender can starve after the last grant.
        const int initial = 100;
        const int target = 200000;
        const int spenderCount = 6;
        RemoteDataWindow window = new(defaultWindowSize: initial);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        long totalSpent = 0;
        Task[] spenders = new Task[spenderCount];
        for (int s = 0; s < spenders.Length; s++)
        {
            int spender = s;
            spenders[s] = Task.Run(async () =>
            {
                Random rng = new(5000 + spender);
                int need = target / spenderCount + (spender == spenderCount - 1 ? target % spenderCount : 0);
                while (need > 0)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    int got = await window.SpendOrWait(Math.Min(need, rng.Next(1, 500)), timeout.Token);
                    need -= got;
                    Interlocked.Add(ref totalSpent, got);
                }
            });
        }
        Task extender = Task.Run(() =>
        {
            Random rng = new(6000);
            while (Volatile.Read(ref totalSpent) < target)
            {
                timeout.Token.ThrowIfCancellationRequested();
                int chunk = rng.Next(1, 1000);
                window.TryExtend(chunk, out _);
            }
        });
        await Task.WhenAll([.. spenders, extender]).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(totalSpent, Is.EqualTo(target), "Every granted byte must be spendable exactly once.");
        Assert.That(window.Available, Is.GreaterThanOrEqualTo(0));
    }

    private static int PickSpend(Random rng)
    {
        int roll = rng.Next(10);
        if (roll < 2)
        {
            return -rng.Next(0, 500);
        }
        if (roll < 4)
        {
            return 0;
        }
        int[] interesting = [1, 499, 500, 501, 999, 1000, 1001, 4000, 1000000];
        return rng.Next(2) == 0 ? interesting[rng.Next(interesting.Length)] : rng.Next(0, 2000);
    }

    private static int PickExtendLength(Random rng)
    {
        int roll = rng.Next(10);
        if (roll < 2)
        {
            return -rng.Next(0, 1000);
        }
        int[] interesting = [0, 1, 999, 1000, 1001, 1000000, int.MaxValue, int.MaxValue - 1000, int.MinValue];
        return rng.Next(2) == 0 ? interesting[rng.Next(interesting.Length)] : rng.Next(0, 5000);
    }
}
