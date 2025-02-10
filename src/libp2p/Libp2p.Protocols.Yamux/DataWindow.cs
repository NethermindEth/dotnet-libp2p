// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Yamux;

/// <summary>
/// Handles remote or local window updates
/// </summary>
internal class DataWindow(int defaultWindowSize = DataWindow.ProtocolInitialWindowSize)
{
    private const int ProtocolInitialWindowSize = 256 * 1024;

    private readonly int _defaultWindowSize = defaultWindowSize;
    private int _available = defaultWindowSize;
    private int _requestedSize;
    private TaskCompletionSource<int>? _windowSizeTcs;
    public int Available { get => _available; }

    /// <summary>
    /// Extends window, typically ruled by local
    /// </summary>
    /// <returns>Window extension depending on statistics</returns>
    public int ExtendWindowIfNeeded()
    {
        if (_available < _defaultWindowSize / 2)
        {
            return ExtendWindow(_defaultWindowSize);
        }

        return 0;
    }

    /// <summary>
    /// Extends window, typically ruled by remote
    /// </summary>
    /// <param name="length">Requested extension</param>
    /// <returns>Requested extension</returns>
    public int ExtendWindow(int length)
    {
        if (length is 0)
        {
            return 0;
        }

        lock (this)
        {
            _available += length;

            if (_windowSizeTcs is not null)
            {
                int availableSize = Math.Min(_requestedSize, _available);
                _available -= availableSize;
                _windowSizeTcs.SetResult(availableSize);
            }

            return length;
        }
    }

    /// <summary>
    /// Spends window up to <paramref name="requestedSize"/> or waits for extension if window is <c>0</c>
    /// </summary>
    /// <param name="requestedSize">Size requested for spending</param>
    /// <returns>Spent size in range of [<c>1</c>, <paramref name="requestedSize"/>]</returns>
    public async Task<int> SpendWindowOrWait(int requestedSize)
    {
        if (requestedSize is 0)
        {
            return 0;
        }
        if (_windowSizeTcs is not null)
        {
            await _windowSizeTcs.Task;
        }

        TaskCompletionSource<int>? taskToWait;

        lock (this)
        {
            if (_available is 0)
            {
                taskToWait = _windowSizeTcs = new();
                _requestedSize = requestedSize;
            }
            else
            {
                int availableSize = Math.Min(requestedSize, _available);
                _available -= availableSize;
                return availableSize;
            }
        }

        return await taskToWait.Task;
    }

    /// <summary>
    /// Spends window, breaks when it's overspent. Typically window is local and spent by remote
    /// </summary>
    /// <param name="requestedSize">Spent size</param>
    /// <returns><see langword="true"/> if spending is in allowed range</returns>
    public bool SpendWindow(int requestedSize)
    {
        int result = Interlocked.Add(ref _available, -requestedSize);
        return result >= 0;
    }
}
