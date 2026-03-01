// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Yamux;

/// <summary>
/// Local window tracking (receive window: how much we allow the remote to send).
/// Tracks incoming data consumption and can extend the window dynamically based on throughput.
/// </summary>
internal class LocalDataWindow
{
    private readonly int _initialWindowSize;
    private readonly int _maxWindowSize;
    private readonly bool _useDynamicWindow;

#pragma warning disable CS9124
    private int _available;
#pragma warning restore CS9124

    private long _consumedSinceLastExtend;
    private long _lastExtendMs;
    private readonly object _extendLock = new();

    public LocalDataWindow(YamuxWindowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.InitialWindowSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.InitialWindowSize, "InitialWindowSize must be positive.");
        }

        if (settings.MaxWindowSize < settings.InitialWindowSize)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.MaxWindowSize, "MaxWindowSize must be at least InitialWindowSize.");
        }

        _initialWindowSize = settings.InitialWindowSize;
        _maxWindowSize = settings.MaxWindowSize;
        _useDynamicWindow = settings.UseDynamicWindow;
        _available = _initialWindowSize;
        _lastExtendMs = Environment.TickCount64;
    }

    public int Available => Volatile.Read(ref _available);

    /// <summary>
    /// Records that <paramref name="bytes"/> have been consumed (handed to the application).
    /// Call this when a write to the upchannel completes, to drive dynamic window sizing.
    /// </summary>
    public void RecordConsumed(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        Interlocked.Add(ref _consumedSinceLastExtend, bytes);
    }

    /// <summary>
    /// Extends the window when below half of initial. When dynamic sizing is enabled,
    /// the extension amount is based on observed consumption rate (throughput).
    /// </summary>
    /// <returns>Number of bytes to advertise in a WindowUpdate, or 0 if no extension.</returns>
    public int ExtendIfNeeded()
    {
        int available = Volatile.Read(ref _available);
        if (available >= _initialWindowSize / 2)
        {
            return 0;
        }

        lock (_extendLock)
        {
            available = Volatile.Read(ref _available);
            if (available >= _initialWindowSize / 2)
            {
                return 0;
            }

            int extension;
            if (_useDynamicWindow)
            {
                long consumed = Interlocked.Exchange(ref _consumedSinceLastExtend, 0);
                long now = Environment.TickCount64;
                long elapsedMs = Math.Max(1, now - _lastExtendMs);
                _lastExtendMs = now;

                double throughputBytesPerSec = consumed * 1000.0 / elapsedMs;
                // Aim to extend by ~0.5s worth of data at current rate, so the sender can stay busy.
                const double windowUpdateIntervalSec = 0.5;
                int dynamicExtension = (int)Math.Min(_maxWindowSize, throughputBytesPerSec * windowUpdateIntervalSec);
                extension = Math.Max(_initialWindowSize, dynamicExtension);
            }
            else
            {
                extension = _initialWindowSize;
            }

            int headroom = _maxWindowSize - available;
            extension = Math.Max(0, Math.Min(extension, headroom));
            if (extension == 0)
            {
                return 0;
            }

            Interlocked.Add(ref _available, extension);
            return extension;
        }
    }

    /// <summary>
    /// Spends window when we receive data from the remote.
    /// </summary>
    /// <param name="requestedSize">Bytes received</param>
    /// <returns><see langword="true"/> if spending is within the allowed window</returns>
    public bool TrySpend(int requestedSize)
    {
        int result = Interlocked.Add(ref _available, -requestedSize);
        return result >= 0;
    }
}
