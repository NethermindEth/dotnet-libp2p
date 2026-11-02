// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataTransferBenchmark;

public sealed record PerfResult(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("timeSeconds")] double TimeSeconds,
    [property: JsonPropertyName("uploadBytes")] ulong UploadBytes,
    [property: JsonPropertyName("downloadBytes")] ulong DownloadBytes);

/// <summary>
/// Prints test-plans perf JSON to stdout: an "intermediary" line every second and one "final" line.
/// Create it BEFORE dialing so the final time includes connection setup.
/// </summary>
public sealed class PerfReporter : IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly System.Threading.Timer _timer;
    private long _lastTicks;
    private long _up, _down, _totalUp, _totalDown;
    private bool _done;

    public PerfReporter()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    public void AddUpload(long bytes)
    {
        Interlocked.Add(ref _up, bytes);
        Interlocked.Add(ref _totalUp, bytes);
    }

    public void AddDownload(long bytes)
    {
        Interlocked.Add(ref _down, bytes);
        Interlocked.Add(ref _totalDown, bytes);
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_done) return;
            long now = _total.ElapsedTicks;
            double seconds = (now - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = now;
            Print(new PerfResult("intermediary", seconds,
                (ulong)Interlocked.Exchange(ref _up, 0),
                (ulong)Interlocked.Exchange(ref _down, 0)));
        }
    }

    public void Final()
    {
        lock (_gate)
        {
            if (_done) return;
            _done = true;
            _timer.Dispose();
            Print(new PerfResult("final", _total.Elapsed.TotalSeconds,
                (ulong)Interlocked.Read(ref _totalUp),
                (ulong)Interlocked.Read(ref _totalDown)));
        }
    }

    public void Dispose()
    {
        lock (_gate) { _done = true; }
        _timer.Dispose();
    }

    private static void Print(PerfResult result) => Console.Out.WriteLine(JsonSerializer.Serialize(result));
}