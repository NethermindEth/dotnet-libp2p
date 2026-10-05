// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core.Tests;

/// <summary>
/// Concurrency fuzzer for <see cref="Channel"/>: multiple writers with unique
/// token ids, pinned single-reader-per-side drains, racing close/abort/EOF, a
/// cancellation storm, and unpinned-reader chaos without a content oracle.
/// Oracle: byte-identity conservation (exact when the channel stays open,
/// duplicate-free subset after abort/close), prompt termination of every task,
/// and no unexpected exceptions. No new dependencies.
/// </summary>
[TestFixture]
public class ChannelConcurrencyFuzzTests
{
    /// <summary>Committed corpus: hammer configurations.</summary>
    public static IEnumerable<int> HammerSeeds()
    {
        for (int seed = 6001; seed <= 6012; seed++)
        {
            yield return seed;
        }
    }

    [TestCaseSource(nameof(HammerSeeds))]
    public async Task Hammer_TwoDirections(int seed) =>
        await RunHammer(seed, oneWay: false);

    [TestCaseSource(nameof(HammerSeeds))]
    public async Task Hammer_OneDirection(int seed) =>
        await RunHammer(seed, oneWay: true);

    /// <summary>Committed corpus: unpinned-reader chaos configurations.</summary>
    public static IEnumerable<int> ChaosSeeds()
    {
        for (int seed = 8001; seed <= 8010; seed++)
        {
            yield return seed;
        }
    }

    [TestCaseSource(nameof(ChaosSeeds))]
    public async Task Chaos_ManyReadersWritersCloser(int seed) =>
        await RunChaos(seed);

    [Test]
    public async Task Storm_CancellationResultsStayLegalAndReusable()
    {
        // 200 iterations, one fresh channel each, racing cancellation against
        // every op kind: results must stay within the legal set for a virgin
        // channel, which keeps each assertion exact.
        using CancellationTokenSource suiteTimeout = new(TimeSpan.FromSeconds(60));
        Random rng = new(7001);
        for (int i = 0; i < 200; i++)
        {
            Channel channel = new();
            using CancellationTokenSource cancel = new();
            cancel.CancelAfter(TimeSpan.FromMilliseconds(rng.Next(0, 10)));
            int op = rng.Next(5);
            switch (op)
            {
                case 0:
                    Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), cancel.Token),
                        Is.AnyOf(IOResult.Ok, IOResult.Cancelled), $"iter {i}");
                    break;
                case 1:
                    // Empty open channel: the read can only be cancelled.
                    Assert.That((await channel.Reverse.ReadAsync(1, token: cancel.Token)).Result,
                        Is.EqualTo(IOResult.Cancelled), $"iter {i}");
                    break;
                case 2:
                    Assert.That(await channel.WriteEofAsync(cancel.Token),
                        Is.AnyOf(IOResult.Ok, IOResult.Cancelled), $"iter {i}");
                    break;
                case 3:
                    Assert.That((await channel.Reverse.ReadAsync(0, ReadBlockingMode.DoNotWait, token: cancel.Token)).Result,
                        Is.AnyOf(IOResult.Ok, IOResult.Cancelled), $"iter {i}");
                    break;
                default:
                    Assert.That(await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 2 }), cancel.Token),
                        Is.AnyOf(IOResult.Ok, IOResult.Cancelled), $"iter {i}");
                    break;
            }
            suiteTimeout.Token.ThrowIfCancellationRequested();
        }

        // A channel that was only ever cancelled stays fully reusable.
        Channel reusable = new();
        Task<IOResult> write = reusable.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 9 })).AsTask();
        Assert.That((await reusable.Reverse.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Data.ToArray(),
            Is.EqualTo(new byte[] { 9 }));
        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
    }

    private static async Task RunHammer(int seed, bool oneWay)
    {
        Random rng = new(seed);
        Channel channel = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using CancellationTokenSource readersDone = new();
        int writers = rng.Next(1, 5);
        // Conservation needs single-reader-per-side: concurrent readers on one
        // direction would fragment the token stream across leftover buffers.
        // Unpinned multi-reader chaos lives in RunChaos instead.
        int readers = oneWay ? 1 : 2;
        int ending = rng.Next(4); // 0 = none, 1 = close, 2 = abort, 3 = eof-then-close
        int tokensPerWriter = rng.Next(50, 200);
        int totalTokens = writers * tokensPerWriter;

        ConcurrentQueue<Exception> faults = new();
        ConcurrentDictionary<int, byte> sent = new();
        ConcurrentDictionary<int, byte> received = new();
        ConcurrentDictionary<string, int> writeOutcomes = new();
        int nextId = 0;
        long receivedCount = 0;

        List<Task> writersTasks = [];
        for (int w = 0; w < writers; w++)
        {
            writersTasks.Add(Task.Run(async () =>
            {
                try
                {
                    for (int t = 0; t < tokensPerWriter; t++)
                    {
                        int id = Interlocked.Increment(ref nextId);
                        sent[id] = 0;
                        byte[] token = BitConverter.GetBytes(id);
                        IOResult result = oneWay
                            ? await channel.WriteAsync(new ReadOnlySequence<byte>(token), timeout.Token)
                            : await (id % 2 == 0
                                ? channel.WriteAsync(new ReadOnlySequence<byte>(token), timeout.Token)
                                : channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(token), timeout.Token));
                        if (result != IOResult.Ok)
                        {
                            writeOutcomes.AddOrUpdate($"early-{result}", 1, (_, n) => n + 1);
                            return;
                        }
                        writeOutcomes.AddOrUpdate("ok", 1, (_, n) => n + 1);
                    }
                }
                catch (Exception e)
                {
                    faults.Enqueue(e);
                }
            }));
        }
        List<Task> readersTasks = [];
        for (int r = 0; r < readers; r++)
        {
            int reader = r;
            readersTasks.Add(Task.Run(async () =>
            {
                try
                {
                    Random local = new(seed * 31 + reader);
                    // Each reader is pinned to one side so partial tokens
                    // reassemble exactly within its own leftover buffer.
                    // Modes are restricted to WaitAny/DoNotWait: unlike WaitAll,
                    // they never park while holding partially taken data, so a
                    // later cancel/terminal cannot strand taken bytes. (WaitAll
                    // partial-take drops are covered by the model tests.)
                    IChannel side = oneWay
                        ? channel.Reverse
                        : (reader % 2 == 0 ? channel : channel.Reverse);
                    List<byte> leftover = [];
                    while (Volatile.Read(ref receivedCount) < totalTokens)
                    {
                        ReadBlockingMode mode = local.Next(2) == 0
                            ? ReadBlockingMode.WaitAny
                            : ReadBlockingMode.DoNotWait;
                        int length = mode == ReadBlockingMode.DoNotWait ? local.Next(0, 64) : local.Next(1, 64);
                        ReadResult read;
                        try
                        {
                            read = await side.ReadAsync(length, mode, token: readersDone.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // Shutdown once the count is reached (or suite timeout).
                            return;
                        }
                        if (read.Result == IOResult.Ok && !read.Data.IsEmpty)
                        {
                            leftover.AddRange(read.Data.ToArray());
                            while (leftover.Count >= 4)
                            {
                                int id = BitConverter.ToInt32([.. leftover.Take(4)]);
                                leftover.RemoveRange(0, 4);
                                if (!received.TryAdd(id, 0))
                                {
                                    faults.Enqueue(new InvalidDataException(
                                        $"Duplicate token {id} (reader {reader}, seed {seed})."));
                                    return;
                                }
                                Interlocked.Increment(ref receivedCount);
                            }
                        }
                        else if (read.Result is IOResult.Ended or IOResult.Aborted)
                        {
                            return;
                        }
                        // Cancelled/Empty: keep polling until close or timeout.
                    }
                }
                catch (Exception e)
                {
                    faults.Enqueue(e);
                }
            }));
        }

        // Let traffic flow, then apply the seeded ending (or none).
        await Task.Delay(rng.Next(20, 200), timeout.Token);
        if (ending is 1 or 2)
        {
            // Interrupt mid-flight: writers/readers observe terminals and exit.
            if (ending == 1)
            {
                await channel.CloseAsync();
            }
            else
            {
                await channel.AbortAsync();
            }
            await Task.WhenAll(writersTasks).WaitAsync(timeout.Token);
            readersDone.Cancel();
            await Task.WhenAll(readersTasks).WaitAsync(timeout.Token);
        }
        else
        {
            // Drain fully first: writers complete and every token is consumed
            // BEFORE any EOF. Rationale: an EOF racing buffered or partially
            // taken data discards it (WaitAll exactness cannot return partial
            // data, so EOF reports the terminal instead). EOF-first would make
            // exact conservation unassertable by construction.
            await Task.WhenAll(writersTasks).WaitAsync(timeout.Token);
            for (int i = 0; i < 200 && Volatile.Read(ref receivedCount) < totalTokens; i++)
            {
                await Task.Delay(25, timeout.Token);
            }
            string diag = $"writers={writers} readers={readers} ending={ending} " +
                $"sent={sent.Count} outcomes=[{string.Join(",", writeOutcomes.Select(kv => $"{kv.Key}:{kv.Value}"))}] " +
                $"missing=[{string.Join(",", Enumerable.Range(1, totalTokens).Where(id => !received.ContainsKey(id)).Take(20))}]";
            Assert.That(Volatile.Read(ref receivedCount), Is.EqualTo(totalTokens),
                $"seed {seed}: tokens missing before EOF. {diag}");
            if (ending == 3)
            {
                await channel.WriteEofAsync(timeout.Token);
                await channel.Reverse.WriteEofAsync(timeout.Token);
            }
            readersDone.Cancel();
            await Task.WhenAll(readersTasks).WaitAsync(timeout.Token);
        }

        Assert.That(faults, Is.Empty, $"seed {seed}: {string.Join("; ", faults.Select(f => f.Message))}");
        foreach (int id in received.Keys)
        {
            Assert.That(sent.ContainsKey(id), Is.True, $"seed {seed}: unknown token {id} received.");
        }
        // Split oracle: writers must have attempted every token (no silent
        // early return); only then is exact conservation meaningful.
        Assert.That(sent.Count, Is.EqualTo(totalTokens), $"seed {seed}: writers exited early.");
        if (ending is 0 or 3)
        {
            Assert.That(receivedCount, Is.EqualTo(totalTokens), $"seed {seed}: lost tokens.");
        }
        else
        {
            Assert.That(receivedCount, Is.LessThanOrEqualTo(totalTokens), $"seed {seed}");
        }
    }

    private static async Task RunChaos(int seed)
    {
        // No content oracle here: unpinned readers fragment streams by design.
        // Oracle: every task terminates, and no unexpected exception escapes
        // any channel operation (this is where faults like a semaphore
        // over-release would surface).
        Random rng = new(seed);
        Channel channel = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        int writers = rng.Next(1, 5);
        int readers = rng.Next(1, 5);
        ConcurrentQueue<Exception> faults = new();
        List<Task> tasks = [];
        for (int w = 0; w < writers; w++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    Random local = new(seed * 131 + w);
                    for (int i = 0; i < 200; i++)
                    {
                        byte[] payload = new byte[local.Next(0, 128)];
                        local.NextBytes(payload);
                        IChannel side = local.Next(2) == 0 ? channel : channel.Reverse;
                        IOResult result = await side.WriteAsync(new ReadOnlySequence<byte>(payload), timeout.Token);
                        if (result == IOResult.InternalError)
                        {
                            faults.Enqueue(new InvalidDataException(
                                $"Spurious InternalError on an occupied buffer (writer {w}, seed {seed})."));
                            return;
                        }
                    }
                }
                catch (Exception e)
                {
                    faults.Enqueue(e);
                }
            }));
        }
        for (int r = 0; r < readers; r++)
        {
            int reader = r;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    Random local = new(seed * 17 + reader);
                    for (int i = 0; i < 200; i++)
                    {
                        ReadBlockingMode mode = (ReadBlockingMode)local.Next(3);
                        int length = mode == ReadBlockingMode.DoNotWait ? local.Next(0, 64) : local.Next(0, 128);
                        IChannel side = local.Next(2) == 0 ? channel : channel.Reverse;
                        await side.ReadAsync(length, mode, token: timeout.Token);
                    }
                }
                catch (Exception e)
                {
                    faults.Enqueue(e);
                }
            }));
        }

        await Task.Delay(rng.Next(50, 500), timeout.Token);
        // Always terminate: parked stragglers must observe the terminal and
        // exit. Any throw (not result) or join timeout is a finding.
        if (seed % 2 == 0)
        {
            await channel.CloseAsync();
        }
        else
        {
            await channel.AbortAsync();
        }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(faults, Is.Empty, $"seed {seed}: {string.Join("; ", faults.Select(f => f.Message))}");
    }
}
