// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Yamux;

/// <summary>
/// Remote window tracking.
/// Single reader and writer in parallel.
/// </summary>
internal class RemoteDataWindow(int defaultWindowSize = YamuxProtocol.ProtocolInitialWindowSize)
{
    private int _available = defaultWindowSize;
    private TaskCompletionSource tcs = new();

    public int Available => Volatile.Read(ref _available);

    /// <summary>
    /// Extends window, according to remote informing for extension
    /// </summary>
    /// <param name="length">Requested extension</param>
    /// <param name="available">Available credit after extension</param>
    /// <returns>Whether the extension fits in the credit counter</returns>
    public bool TryExtend(int length, out int available)
    {
        while (true)
        {
            int current = Volatile.Read(ref _available);
            if (length < 0 || length > int.MaxValue - current)
            {
                available = current;
                return false;
            }

            available = current + length;
            if (Interlocked.CompareExchange(ref _available, available, current) != current)
                continue;

            if (available > 0)
                Volatile.Read(ref tcs).TrySetResult();
            return true;
        }
    }

    /// <summary>
    /// Spends window up to <paramref name="requestedSize"/> or waits for extension if window is <c>0</c>, depending on how much is sent to remote.
    /// </summary>
    /// <param name="requestedSize">Size requested for spending</param>
    /// <returns>Spent size in range of [<c>1</c>, <paramref name="requestedSize"/>]</returns>
    public async Task<int> SpendOrWait(int requestedSize, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedSize);
        while (true)
        {
            int available = Volatile.Read(ref _available);
            if (available > 0)
            {
                int spent = Math.Min(requestedSize, available);
                if (Interlocked.CompareExchange(ref _available, available - spent, available) == available)
                    return spent;
                continue;
            }

            TaskCompletionSource signal = Volatile.Read(ref tcs);
            if (Volatile.Read(ref _available) > 0)
                continue;
            await signal.Task.WaitAsync(token);
            Interlocked.CompareExchange(ref tcs, new TaskCompletionSource(), signal);
        }
    }
}
