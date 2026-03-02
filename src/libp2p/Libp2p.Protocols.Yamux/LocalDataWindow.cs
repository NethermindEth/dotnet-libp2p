// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Yamux;

/// <summary>
/// Local window tracking.
/// Single reader and writer in parallel.
/// </summary>
internal class LocalDataWindow(int defaultWindowSize = YamuxProtocol.ProtocolInitialWindowSize)
{
#pragma warning disable CS9124
    private int _available = defaultWindowSize;
#pragma warning restore CS9124

    public int Available { get => Volatile.Read(ref _available); }

    /// <summary>
    /// Extends window by local, uses simple strategy of adding window size when less the half available.
    /// </summary>
    /// <returns>Window extension depending on statistics</returns>
    public int ExtendIfNeeded()
    {
        if (_available < defaultWindowSize / 2)
        {
            int length = defaultWindowSize;
            Interlocked.Add(ref _available, length);
            return length;
        }

        return 0;
    }

    /// <summary>
    /// Spends window by remote, breaks when it's overspent.
    /// </summary>
    /// <param name="requestedSize">Spent size</param>
    /// <returns><see langword="true"/> if spending is in allowed range</returns>
    public bool TrySpend(int requestedSize)
    {
        int result = Interlocked.Add(ref _available, -requestedSize);
        return result >= 0;
    }
}
