// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Libp2p.Core.Metrics;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.TestsBase")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Benchmarks")]
[assembly: InternalsVisibleTo("Libp2p.Protocols.Pubsub.Tests")]

namespace Nethermind.Libp2p.Core;

public class Channel : IChannel
{
    private IChannel? _reversedChannel;
    private ReaderWriter _reader;
    private ReaderWriter _writer;
    private TaskCompletionSource Completion = new();

    public Channel()
    {
        _reader = new ReaderWriter(this);
        _writer = new ReaderWriter(this);
    }

    private Channel(ReaderWriter reader, ReaderWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public IChannel Reverse
    {
        get => _reversedChannel ??= new Channel((ReaderWriter)Writer, (ReaderWriter)Reader)
        {
            _reversedChannel = this,
            Completion = Completion
        };
    }

    public IReader Reader { get => _reader; }
    public IWriter Writer { get => _writer; }


    public ValueTask<ReadResult> ReadAsync(int length,
        ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default)
    {
        return Reader.ReadAsync(length, blockingMode, token);
    }

    public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        return Writer.WriteAsync(bytes, token);
    }

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => Writer.WriteEofAsync(token);

    public TaskAwaiter GetAwaiter() => Completion.Task.GetAwaiter();

    public async ValueTask CloseAsync()
    {
        ValueTask<IOResult> stopReader = _reader.WriteEofAsync().Preserve();
        await _writer.WriteEofAsync().ConfigureAwait(false);
        if (!stopReader.IsCompleted)
        {
            await stopReader.ConfigureAwait(false);
        }
        Completion.TrySetResult();
    }

    private void TryComplete()
    {
        if (_reader._eow && _writer._eow)
        {
            Completion.TrySetResult();
        }
    }


    internal class ReaderWriter : IReader, IWriter
    {
        internal protected ReaderWriter(Channel tryComplete)
        {
            _externalCompletionMonitor = tryComplete;
        }

        public ReaderWriter()
        {
        }

        private ReadOnlySequence<byte> _bytes;
        private readonly SemaphoreSlim _canWrite = new(1, 1);
        private readonly SemaphoreSlim _read = new(0, 1);
        private readonly SemaphoreSlim _canRead = new(0, 1);
        private readonly SemaphoreSlim _readLock = new(1, 1);
        private readonly Channel? _externalCompletionMonitor;
        internal bool _eow = false;

        public async ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
            CancellationToken token = default)
        {
            bool readLockTaken = false;
            try
            {
                await _readLock.WaitAsync(token).ConfigureAwait(false);
                readLockTaken = true;

                if (_eow)
                {
                    return ReadResult.Ended;
                }

                if (blockingMode == ReadBlockingMode.DoNotWait && _bytes.Length == 0)
                {
                    return ReadResult.Empty;
                }

                // Handle zero-length reads immediately to avoid deadlock with empty protobuf messages
                // WriteAsync returns early for zero-length writes without signaling _canRead
                // Only apply this optimization for WaitAll mode (exact length reads)
                // For WaitAny mode (ReadAllAsync), we need to wait for actual data
                if (length == 0 && blockingMode == ReadBlockingMode.WaitAll)
                {
                    return ReadResult.Ok(default);
                }

                await _canRead.WaitAsync(token).ConfigureAwait(false);

                if (_eow)
                {
                    _canRead.Release();
                    _read.Release();
                    return ReadResult.Ended;
                }

                bool lockAgain = false;
                long bytesToRead = length != 0
                    ? (blockingMode == ReadBlockingMode.WaitAll ? length : Math.Min(length, _bytes.Length))
                    : _bytes.Length;

                ReadOnlySequence<byte> chunk = default;
                do
                {
                    if (lockAgain) await _canRead.WaitAsync(token).ConfigureAwait(false);

                    if (_eow)
                    {
                        _canRead.Release();
                        _read.Release();
                        return ReadResult.Ended;
                    }

                    ReadOnlySequence<byte> anotherChunk = default;

                    if (_bytes.Length <= bytesToRead)
                    {
                        anotherChunk = _bytes;
                        bytesToRead -= _bytes.Length;
                        _bytes = default;
                        _read.Release();
                        _canWrite.Release();
                    }
                    else if (_bytes.Length > bytesToRead)
                    {
                        anotherChunk = _bytes.Slice(0, bytesToRead);
                        _bytes = _bytes.Slice(bytesToRead, _bytes.End);
                        bytesToRead = 0;
                        _canRead.Release();
                    }

                    chunk = chunk.Length == 0 ? anotherChunk : chunk.Append(anotherChunk.First);
                    lockAgain = true;
                } while (bytesToRead != 0);

                Libp2pMetrics.DataReceivedBytes.Add(chunk.Length);
                Libp2pMetrics.DataReceivedPackets.Add(1);
                return ReadResult.Ok(chunk);
            }
            catch (OperationCanceledException)
            {
                return ReadResult.Cancelled;
            }
            finally
            {
                // Release the read lock on every path, including cancellation, so a cancelled
                // read can never leave it held and deadlock every subsequent read.
                if (readLockTaken)
                {
                    _readLock.Release();
                }
            }
        }

        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            bool canWriteTaken = false;
            try
            {
                await _canWrite.WaitAsync(token).ConfigureAwait(false);
                canWriteTaken = true;

                if (_eow)
                {
                    return IOResult.Ended;
                }

                if (_bytes.Length != 0)
                {
                    return IOResult.InternalError;
                }

                if (bytes.Length == 0)
                {
                    return IOResult.Ok;
                }

                _bytes = bytes;
                _canRead.Release();

                try
                {
                    await _read.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled after publishing the chunk. If no reader has taken the
                    // data-available signal yet, reclaim it and roll the publish back so the
                    // cancelled bytes are never delivered to a later read. Otherwise a reader
                    // is already committed to consuming, so wait (uncancellably) for it to
                    // finish and hand back _read/_canWrite, keeping the channel consistent.
                    if (_canRead.Wait(0))
                    {
                        _bytes = default;
                    }
                    else
                    {
                        await _read.WaitAsync().ConfigureAwait(false);
                        canWriteTaken = false;
                    }

                    throw;
                }

                // The reader consumed the chunk and released _read/_canWrite on our behalf.
                canWriteTaken = false;
                Libp2pMetrics.DataSentBytes.Add(bytes.Length);
                Libp2pMetrics.DataSentPackets.Add(1);
                return IOResult.Ok;
            }
            catch (OperationCanceledException)
            {
                return IOResult.Cancelled;
            }
            finally
            {
                // Release the write lock on every path we still own it (early exits and a
                // rolled-back cancellation); on the success path the reader releases it for us.
                if (canWriteTaken)
                {
                    _canWrite.Release();
                }
            }
        }

        public async ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
        {
            try
            {
                await _canWrite.WaitAsync(token).ConfigureAwait(false);

                if (_eow)
                {
                    _canWrite.Release();
                    return IOResult.Ended;
                }
                _eow = true;
                _externalCompletionMonitor?.TryComplete();
                _canRead.Release();
                _canWrite.Release();
                return IOResult.Ok;
            }
            catch (TaskCanceledException)
            {
                return IOResult.Cancelled;
            }
        }

        public async ValueTask<IOResult> CanReadAsync(CancellationToken token = default)
        {
            try
            {
                if (_eow)
                {
                    return IOResult.Ended;
                }
                await _readLock.WaitAsync(token).ConfigureAwait(false);
                _readLock.Release();
                return !_eow ? IOResult.Ok : IOResult.Ended;
            }
            catch (TaskCanceledException)
            {
                return IOResult.Cancelled;
            }
        }
    }
}
