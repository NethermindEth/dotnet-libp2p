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
    /// <returns>Requested extension</returns>
    public int Extend(int length)
    {
        if (length == 0)
        {
            return 0;
        }

        if (length < 0)
        {
            throw new ArgumentException("Cannot be negative", nameof(length));
        }

        int updatedAvailable = Interlocked.Add(ref _available, length);
        if (updatedAvailable > 0)
        {
            tcs.TrySetResult();
        }

        return length;
    }

    /// <summary>
    /// Spends window up to <paramref name="requestedSize"/> or waits for extension if window is <c>0</c>, depending on how much is sent to remote.
    /// </summary>
    /// <param name="requestedSize">Size requested for spending</param>
    /// <returns>Spent size in range of [<c>1</c>, <paramref name="requestedSize"/>]</returns>
    public async Task<int> SpendOrWait(int requestedSize, CancellationToken token = default)
    {
        int updatedAvailable = Interlocked.Add(ref _available, -requestedSize);

        if (updatedAvailable >= 0)
        {
            return requestedSize;
        }
        else if (updatedAvailable > -requestedSize)
        {
            int spent = requestedSize + updatedAvailable;
            Interlocked.Add(ref _available, -updatedAvailable);
            return spent;
        }

        Interlocked.Add(ref _available, requestedSize);
        await tcs.Task.WaitAsync(token);
        Interlocked.CompareExchange(ref tcs, new TaskCompletionSource(), tcs);

        return await SpendOrWait(requestedSize, token);
    }
}
