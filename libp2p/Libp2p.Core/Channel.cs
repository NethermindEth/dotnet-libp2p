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
[assembly: InternalsVisibleTo("Libp2p.Protocols.Pubsub.Tests")]

namespace Nethermind.Libp2p.Core;

/// <summary>Adapts the shared channel engine to libp2p's framing helpers and metrics.</summary>
public class Channel : IChannel
{
    private readonly SharedChannel _channel;
    private IChannel? _reversedChannel;

    public Channel() : this(new SharedChannel()) { }

    private Channel(SharedChannel channel)
    {
        _channel = channel;
        Reader = new ReaderWriter((SharedChannel.ReaderWriter)channel.Reader);
        Writer = new ReaderWriter((SharedChannel.ReaderWriter)channel.Writer);
    }

    public IChannel Reverse => _reversedChannel ??= new Channel((SharedChannel)_channel.Reverse) { _reversedChannel = this };
    public IReader Reader { get; }
    public IWriter Writer { get; }

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

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => Writer.WriteEofAsync(token);
    public ValueTask CloseAsync() => _channel.CloseAsync();
    public ValueTask AbortAsync() => _channel.AbortAsync();
    public ValueTask CloseReadAsync() => _channel.CloseReadAsync();
    internal ValueTask CompleteProtocolAsync() => _channel.CompleteProtocolAsync();
    public TaskAwaiter GetAwaiter() => _channel.GetAwaiter();

    internal sealed class ReaderWriter : IReader, IWriter
    {
        internal SharedChannel.ReaderWriter Inner { get; }
        internal bool _eow => Inner.IsWriteEnded;

        public ReaderWriter() : this(new SharedChannel.ReaderWriter()) { }
        internal ReaderWriter(SharedChannel.ReaderWriter inner) => Inner = inner;
        internal bool TryWriteEof() => Inner.TryWriteEof();

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

        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
            => RecordWrite(await Inner.WriteAsync(bytes, token).ConfigureAwait(false), bytes.Length);

        public async ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
            => RecordWrite(await Inner.WriteAsync(buffer, length, offset, token).ConfigureAwait(false), length);

        public async ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
            => RecordWrite(await Inner.WriteAsync(slice, token).ConfigureAwait(false), slice.Length);

        private static IOResult RecordWrite(Channels.IOResult result, long length)
        {
            if (result == Channels.IOResult.Ok && length != 0)
            {
                Libp2pMetrics.DataSentBytes.Add(length);
                Libp2pMetrics.DataSentPackets.Add(1);
            }
            return (IOResult)result;
        }

        public async ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
            => (IOResult)await Inner.WriteEofAsync(token).ConfigureAwait(false);

        public async ValueTask<IOResult> CanReadAsync(CancellationToken token = default)
            => (IOResult)await Inner.CanReadAsync(token).ConfigureAwait(false);
    }
}
