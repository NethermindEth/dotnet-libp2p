// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Channels;
using Nethermind.Libp2p.Core.Metrics;
using SharedChannel = Nethermind.Channels.Channel;
using SharedReadResult = Nethermind.Channels.ReadResult;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.TestsBase")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Benchmarks")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.WebRtc")]
[assembly: InternalsVisibleTo("Libp2p.Protocols.Pubsub.Tests")]
[assembly: InternalsVisibleTo("Libp2p.E2eTests")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.KadDht.Tests")]

namespace Nethermind.Libp2p.Core;

/// <summary>Adapts the shared channel engine to libp2p's framing helpers and metrics.</summary>
public class Channel : IChannel
{
    private readonly SharedChannel _channel;
    private readonly ReaderWriter _reader;
    private readonly ReaderWriter _writer;
    private IChannel? _reversedChannel;

    public Channel() : this(countInboundWritesAsSent: true) { }

    internal Channel(bool countInboundWritesAsSent)
    {
        _channel = new SharedChannel();
        _reader = new ReaderWriter((SharedChannel.ReaderWriter)_channel.Reader, countInboundWritesAsSent);
        _writer = new ReaderWriter((SharedChannel.ReaderWriter)_channel.Writer);
    }

    private Channel(SharedChannel channel, ReaderWriter reader, ReaderWriter writer)
    {
        _channel = channel;
        _reader = reader;
        _writer = writer;
    }

    public IChannel Reverse => _reversedChannel ??= new Channel((SharedChannel)_channel.Reverse, _writer, _reader) { _reversedChannel = this };
    public IReader Reader => _reader;
    public IWriter Writer => _writer;

    public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default) => Reader.ReadAsync(length, blockingMode, token);

    public ValueTask<ReadResult> ReadOwnedAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default) => Reader.ReadOwnedAsync(length, blockingMode, token);

    public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        => Writer.WriteAsync(bytes, token);

    public ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
        => Writer.WriteAsync(buffer, length, offset, token);

    public ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
        => Writer.WriteAsync(slice, token);

    public ValueTask<IOResult> WriteFromAsync(Func<Memory<byte>, CancellationToken, ValueTask<int>> readAsync,
        int bufferSize, CancellationToken token = default) => Writer.WriteFromAsync(readAsync, bufferSize, token);

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => Writer.WriteEofAsync(token);
    public ValueTask CloseAsync() => _channel.CloseAsync();
    public ValueTask AbortAsync() => _channel.AbortAsync();
    public ValueTask CloseReadAsync() => _channel.CloseReadAsync();
    internal ValueTask CompleteProtocolAsync() => _channel.CompleteProtocolAsync();
    public TaskAwaiter GetAwaiter() => _channel.GetAwaiter();

    internal sealed class ReaderWriter : IReader, IWriter
    {
        private readonly bool _countSent;
        internal SharedChannel.ReaderWriter Inner { get; }
        internal bool _eow => Inner.IsWriteEnded;

        public ReaderWriter() : this(new SharedChannel.ReaderWriter()) { }
        internal ReaderWriter(SharedChannel.ReaderWriter inner, bool countSent = true)
        {
            Inner = inner;
            _countSent = countSent;
        }
        internal bool TryWriteEof() => Inner.TryWriteEof();

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            using SharedReadResult result = await Inner.ReadAsync(length, (Channels.ReadBlockingMode)blockingMode, token).ConfigureAwait(false);
            // Legacy sequences can outlive the read without disposal. Borrowed writes already
            // have that lifetime; pooled results must be detached before releasing their lease.
            ReadOnlySequence<byte> data = result.IsOwned ? new(result.Sequence.ToArray()) : result.Sequence;
            RecordRead(result);
            return new ReadResult { Result = (IOResult)result.Result, Data = data };
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<ReadResult> ReadOwnedAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            SharedReadResult result = await Inner.ReadAsync(length, (Channels.ReadBlockingMode)blockingMode, token).ConfigureAwait(false);
            try
            {
                RecordRead(result);
                return ReadResult.FromOwned(result);
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static void RecordRead(SharedReadResult result)
        {
            if (result.Result != Channels.IOResult.Ok || result.Sequence.IsEmpty) return;
            Libp2pMetrics.DataReceivedBytes.Add(result.Sequence.Length);
            Libp2pMetrics.DataReceivedPackets.Add(1);
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
            => RecordWrite(await Inner.WriteAsync(bytes, token).ConfigureAwait(false), bytes.Length);

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
            => RecordWrite(await Inner.WriteAsync(buffer, length, offset, token).ConfigureAwait(false), length);

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
            => RecordWrite(await Inner.WriteAsync(slice, token).ConfigureAwait(false), slice.Length);

        public async ValueTask<IOResult> WriteFromAsync(Func<Memory<byte>, CancellationToken, ValueTask<int>> readAsync,
            int bufferSize, CancellationToken token = default)
            => (IOResult)await WriterExtensions.WriteFromAsync(Inner.WriteAsync, readAsync, bufferSize,
                ArrayPool<byte>.Shared, token, _countSent ? static length => RecordSent(length) : null).ConfigureAwait(false);

        private IOResult RecordWrite(Channels.IOResult result, long length)
        {
            if (_countSent && result == Channels.IOResult.Ok && length != 0) RecordSent(length);
            return (IOResult)result;
        }

        private static void RecordSent(long length)
        {
            Libp2pMetrics.DataSentBytes.Add(length);
            Libp2pMetrics.DataSentPackets.Add(1);
        }

        public async ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
            => (IOResult)await Inner.WriteEofAsync(token).ConfigureAwait(false);

        public async ValueTask<IOResult> CanReadAsync(CancellationToken token = default)
            => (IOResult)await Inner.CanReadAsync(token).ConfigureAwait(false);
    }
}
