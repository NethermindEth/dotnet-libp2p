// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Nethermind.Libp2p.Core.Metrics;

namespace Nethermind.Libp2p.Core.TestsBase;

/// <summary>Records one long-valued instrument for a protocol.</summary>
public sealed class MetricRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    public ConcurrentQueue<(long Value, KeyValuePair<string, object?>[] Tags)> Measurements { get; } = new();

    public MetricRecorder(string instrumentName, string protocolId, Action? onMeasurement = null)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == Libp2pMetrics.MeterName && instrument.Name == instrumentName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var snapshot = tags.ToArray();
            if (snapshot.Any(tag => tag.Key == "protocol" && Equals(tag.Value, protocolId)))
            {
                Measurements.Enqueue((value, snapshot));
                onMeasurement?.Invoke();
            }
        });
        _listener.Start();
    }

    public long Total(string direction) => Measurements
        .Where(m => m.Tags.Any(tag => tag.Key == "direction" && Equals(tag.Value, direction)))
        .Sum(m => m.Value);

    public void Dispose() => _listener.Dispose();
}
