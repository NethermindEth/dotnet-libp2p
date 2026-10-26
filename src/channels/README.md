# Channels and pooled buffers

`Nethermind.Channels` supplies the byte-channel engine used by libp2p. It is included in
`libp2p/Libp2p.slnx`, the standalone `Channels.slnx`, CI tests, and package publishing.

## Ownership

`PooledBuffer.Rent(length)` owns one reference to pooled storage. Its `Memory` and `Span`
expose only the requested length, even if the pool supplies a larger array. The managed
array header is never changed, and no implicit array conversion exposes unused capacity.

A slice, a channel publication, and an owned read each retain an independent reference.
Disposing a lease releases only its reference and is idempotent. A copied `ReadResult`
shares the same lease; call `ToSlice()` to create an independent reference. Storage returns
to the pool after the final reference is released. Memory must not be accessed after its
lease is disposed. Retaining can race with disposal, but accessing memory and disposing
that same lease must not race; use an independent slice when ownership crosses tasks.

```csharp
Channel channel = new();
using PooledBuffer buffer = PooledBuffer.Rent(4);
new byte[] { 1, 2, 3, 4 }.CopyTo(buffer.Memory);
Task<IOResult> writing = channel.WriteAsync(buffer, 4).AsTask();
using ReadResult read = await channel.Reverse.ReadAsync(4);
await writing;
Console.WriteLine(read.Sequence.Length);
```

Published bytes must remain unchanged while readers retain them. A write completing means
that its bytes have been taken by a reader; it does not mean the reader has finished using
them. Caller disposal is safe because channel writes acquire their own reference before
returning, including while a write waits behind another publication.

## Reading and forwarding

`ReadAsync` returns a disposable `ReadResult`. `Sequence` works for every result. `Memory`
and `Data` expose contiguous results; a segmented borrowed result must use `Sequence`.
A single owned chunk is read without copying. An owned `WaitAll` spanning publications
accumulates into contiguous pooled storage, growing with bytes received rather than a
potentially large requested length. Borrowed sequence writes remain segmented without
copying. An atomic multi-slice write coalesces its inputs into one pooled publication.

```csharp
using ReadResult read = await incoming.ReadAsync(length).OrThrow();
using PooledBuffer.Slice forwarding = read.ToSlice();
await outgoing.WriteAsync(forwarding);
```

`ReadAllAsync` keeps the current result alive for its iteration and disposes it when the
enumerator advances or is disposed. Retain a slice to keep owned bytes beyond an iteration.
Borrowed writes have no pool lease and can be forwarded through `WriteAsync(read.Sequence)`.

`WaitAll` waits for the requested length, `WaitAny` takes the available prefix, and
`DoNotWait` returns an empty successful result when no data is available. Cancellation
retracts a publication only before any bytes are consumed; after partial consumption the
remainder stays owed until delivery or channel teardown. `WriteEofAsync`, `CloseAsync`,
`CloseReadAsync`, and `AbortAsync` preserve libp2p's existing half-close and abort semantics.

Read and write operations reuse asynchronous state through .NET's pooled ValueTask builder.
Await each returned `ValueTask` once. If an operation needs blocking or multiple awaits,
call `AsTask()` once and use that task; `ChannelStream` handles this for synchronous callers.

## Receiving

Use the shared callback pump to keep allocation and lifetime handling inside the channel API:

```csharp
await writer.WriteFromAsync(stream.ReadAsync, bufferSize: 64 * 1024, token);
```

The callback fills the supplied memory, accepts short destinations, and returns its written
length or zero for EOF. It must stop accessing the memory when it completes. The pump
waits for channel acknowledgement before receiving again. It reuses storage only when the
pump owns its sole remaining reference; otherwise it fills unused regions or rents another
buffer. Source exceptions propagate, unsuccessful writes stop receiving, and all pump
references are released on exit. The transport decides when to publish EOF or close.

This bounds receive-buffer amplification from tiny fragments. It does not impose a peer
memory limit: consumers retaining reads, message limits, and active receive buffers still
contribute to memory use.

## Libp2p compatibility

`Nethermind.Libp2p.Core.Channel` adapts this engine to existing libp2p types and metrics.
Existing `ReadAsync` returns stable, indefinitely retainable sequences: borrowed data passes
through unchanged; pooled data is copied before its lease is released. `ReadOwnedAsync`
returns a disposable Core `ReadResult` without that compatibility copy. Core framing helpers
and `ChannelStream` use owned reads internally. `ReadAllOwnedAsync` scopes each result to
its iteration. Custom channels that implement only the original API continue to work through
default fallbacks; custom pooled writers should implement the ownership overloads to avoid
compatibility copies.

```csharp
using Nethermind.Libp2p.Core;

using ReadResult read = await channel.ReadOwnedAsync(length);
ReadOnlySequence<byte> data = read.OrThrow();
// Consume data before read is disposed.
```

See [allocation measurements](Nethermind.Channels.Benchmarks/README.md) for before/after
results, including cases where lease bookkeeping costs more than managed storage.
