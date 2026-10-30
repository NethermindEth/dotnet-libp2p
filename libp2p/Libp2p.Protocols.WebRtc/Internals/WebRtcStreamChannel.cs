// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// Presents a <see cref="WebRtcStream"/> as the <see cref="IChannel"/> the rest of the stack speaks,
/// so an application stream can be handed to context.Upgrade and the Noise handshake can run over it.
/// </summary>
internal sealed class WebRtcStreamChannel : IChannel
{
    private readonly WebRtcStream _stream;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Payload of the last frame, and how much of it the caller has taken. WebRTC framing splits a
    /// write at boundaries the reader never asked about, so a read spans frames and keeps the rest.
    /// </summary>
    private byte[]? _frame;
    private int _offset;

    private bool _ended;
    private int _aborted;

    /// <summary>
    /// Which halves are finished. As in <see cref="Channel"/>, the awaiter fires only once both
    /// are: a channel whose write half ended is still readable, and firing early would cancel
    /// <see cref="IChannel.CancellationToken"/> out from under a caller still using it.
    /// Interlocked rather than volatile so the two halves cannot both miss each other's store.
    /// </summary>
    private int _writesDone;
    private int _readsDone;

    public WebRtcStreamChannel(WebRtcStream stream) => _stream = stream;

    /// <summary>The SCTP stream this channel runs on; see <see cref="IWebRtcDataChannel.StreamId"/>.</summary>
    public ushort StreamId => _stream.StreamId;

    public TaskAwaiter GetAwaiter() => _completion.Task.GetAwaiter();

    public async ValueTask<ReadResult> ReadAsync(int length,
        ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (Volatile.Read(ref _aborted) != 0)
        {
            return ReadResult.Aborted;
        }

        // As in Channel: an exact read of nothing is already satisfied, and must not park on a peer
        // that owes no bytes.
        if (length == 0 && blockingMode == ReadBlockingMode.WaitAll)
        {
            return ReadResult.Empty;
        }

        List<ReadOnlyMemory<byte>> parts = [];
        int collected = 0;

        while (blockingMode == ReadBlockingMode.WaitAll ? collected < length : collected == 0)
        {
            if (_frame is not null && _offset < _frame.Length)
            {
                int take = length == 0
                    ? _frame.Length - _offset
                    : Math.Min(length - collected, _frame.Length - _offset);
                parts.Add(new ReadOnlyMemory<byte>(_frame, _offset, take));
                _offset += take;
                collected += take;
                continue;
            }

            if (_ended)
            {
                // A WaitAll that cannot be filled reports the end rather than a short read: bytes
                // returned as Ok are indistinguishable from a complete answer.
                EndReads();
                return ReadResult.Ended;
            }

            if (blockingMode == ReadBlockingMode.DoNotWait)
            {
                // WebRtcStream offers no non-blocking read, so this serves only what is already
                // buffered here. Callers use DoNotWait to poll for an abort, not to drain data.
                break;
            }

            byte[]? next;
            try
            {
                next = await _stream.ReadAsync(token);
            }
            catch (OperationCanceledException)
            {
                return Volatile.Read(ref _aborted) != 0 ? ReadResult.Aborted : ReadResult.Cancelled;
            }
            catch (Exception)
            {
                // A peer RESET_STREAM, and any other way the stream dies mid-flight, faults the
                // reader here. It must never degrade to Ended: a reader told the stream ended
                // believes it received the whole message, so a reset would become silent truncation.
                Abort();
                return ReadResult.Aborted;
            }

            if (next is null)
            {
                _ended = true;
                continue;
            }

            _frame = next;
            _offset = 0;
        }

        return collected == 0
            ? ReadResult.Empty
            : new ReadResult { Result = IOResult.Ok, Data = AsSequence(parts) };
    }

    public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        if (Volatile.Read(ref _aborted) != 0)
        {
            return IOResult.Aborted;
        }

        try
        {
            await _stream.WriteAsync(bytes.IsSingleSegment ? bytes.First : bytes.ToArray(), token);
            return IOResult.Ok;
        }
        catch (OperationCanceledException)
        {
            return IOResult.Cancelled;
        }
        catch (InvalidOperationException)
        {
            // The write half is already closed by our own FIN, which is an orderly end, not a fault.
            return IOResult.Ended;
        }
        catch (Exception)
        {
            Abort();
            return IOResult.Aborted;
        }
    }

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => CloseWritesAsync(token);

    public async ValueTask CloseAsync()
    {
        await CloseWritesAsync(CancellationToken.None);

        // Unlike WriteEofAsync, this ends the channel as a whole, so it completes the awaiter even
        // with the read half still open -- the same split Channel.CloseAsync makes.
        _completion.TrySetResult();
    }

    public async ValueTask CloseReadAsync()
    {
        try
        {
            await _stream.StopReadingAsync();
            EndReads();
        }
        catch (Exception)
        {
            // Teardown must not throw: the IChannel close calls return no result, and Yamux
            // discards this very task (YamuxProtocol.AbortStream), so anything escaping here would
            // reach TaskScheduler.UnobservedTaskException. The stream is already dead, which is
            // what the caller wanted; record it so a later read reports Aborted rather than Ended.
            Abort();
        }
    }

    public async ValueTask AbortAsync()
    {
        // Set before the await, so a read racing the abort reports Aborted instead of parking.
        Interlocked.Exchange(ref _aborted, 1);
        try
        {
            await _stream.ResetAsync();
        }
        finally
        {
            Abort();
        }
    }

    private async ValueTask<IOResult> CloseWritesAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _aborted) != 0)
        {
            return IOResult.Aborted;
        }

        try
        {
            // Idempotent in WebRtcStream, so WriteEofAsync followed by CloseAsync still sends one FIN.
            await _stream.CloseWritesAsync(token);
            Interlocked.Exchange(ref _writesDone, 1);
            TryComplete();
            return IOResult.Ok;
        }
        catch (OperationCanceledException)
        {
            return IOResult.Cancelled;
        }
        catch (Exception)
        {
            Abort();
            return IOResult.Aborted;
        }
    }

    private void EndReads()
    {
        Interlocked.Exchange(ref _readsDone, 1);
        TryComplete();
    }

    private void Abort()
    {
        Interlocked.Exchange(ref _aborted, 1);
        Interlocked.Exchange(ref _readsDone, 1);
        Interlocked.Exchange(ref _writesDone, 1);
        _completion.TrySetResult();
    }

    private void TryComplete()
    {
        if (Volatile.Read(ref _writesDone) != 0 && Volatile.Read(ref _readsDone) != 0)
        {
            _completion.TrySetResult();
        }
    }

    private static ReadOnlySequence<byte> AsSequence(List<ReadOnlyMemory<byte>> parts)
    {
        if (parts.Count == 1)
        {
            return new ReadOnlySequence<byte>(parts[0]);
        }

        MemorySegment<byte> first = new(parts[0]);
        MemorySegment<byte> last = first;
        for (int i = 1; i < parts.Count; i++)
        {
            last = last.Append(parts[i]);
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
}
