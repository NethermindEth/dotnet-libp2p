# Channels & Buffers: Fast Path Guide

Think of this as the keynote: minimal allocations, minimal copies, and explicit ownership.

## Buffers (PooledBuffer)

- **Rent**: `var buf = PooledBuffer.Rent(size);` — one link on creation.
- **Spans without copies**: `buf.Span` / `buf.ReadOnlySpan` / `buf.Memory` expose the rented array directly.
- **Cast helpers**: implicit casts let you pass `PooledBuffer` where `Span<byte>`, `ReadOnlySpan<byte>`, `Memory<byte>`, `ReadOnlyMemory<byte>`, or `byte[]` are expected, without copying.
- **Slices keep sharing**: `var slice = buf[offset...(offset+length)];` adds a ref-count link so the parent stays alive while the slice lives. Disposal (or `Complete`) decrements that link.
- **Ref counting**: `buf.RefCount` shows how many active links exist (caller, channel, read results, slices). `Dispose()` drops one link; the underlying array returns to the pool when links reach zero.

### Buffer example: create, fill, slice, forward

```csharp
PooledBuffer buf = PooledBuffer.Rent(16);
Encoding.UTF8.GetBytes("hello buffers", buf.Span);

PooledBuffer.Slice slice = buf[0..5]; // adds a link
await channel.WriteAsync(slice); // zero extra copies
slice.Dispose();                 // drop slice link
buf.Dispose();                   // drop caller link; pool gets buffer if no other links
```

## Channels

Channels move pooled buffers with predictable ownership. Reads/writes keep allocations and copies to a minimum:

- **Writes**: `WriteAsync(PooledBuffer buffer, int length, int offset = 0)` or `WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices)` send existing buffers; no payload copies.
- **Reads**: `ReadAsync(int length, ReadBlockingMode mode = WaitAll)` returns a `ReadResult` that owns a buffer link. Data is exposed as `ReadOnlySpan<byte> Data`.
- **ReadAll**: `await foreach (var slice in reader.ReadAllAsync())` streams `Slice` instances; each slice holds its own link, letting you forward without copying.
- **Line/varint helpers**: `ReadLineAsync`, `WriteLineAsync`, `ReadVarintAsync`, `WriteVarintAsync` encode/decode in pooled buffers.
- **End-of-write**: `WriteEofAsync()` signals readers with `IOResult.Ended`.
- **Blocking modes**: `WaitAll` coalesces multi-buffer reads into one contiguous rental only when needed; `WaitAny` returns as soon as data is present; `DontWait` returns `ReadResult.Empty` if nothing is ready.

### Channel example: write once, read once

```csharp
PooledBuffer buf = PooledBuffer.Rent(12);
Encoding.UTF8.GetBytes("hello world!", buf.Span);

IChannel channel = new Channel();
IChannel remote = channel.Reverse;

await channel.WriteAsync(buf, 12);
buf.Dispose(); // caller link released; channel holds its own link

using ReadResult read = await remote.ReadAsync(12, ReadBlockingMode.WaitAll);
Console.WriteLine(Encoding.UTF8.GetString(read.Data));
// read.Dispose() releases the channel-held link
```

### Forwarding without copies: read → slice → next channel

```csharp
IChannel sink = new Channel();
IChannel source = sink.Reverse;
IChannel next = new Channel();
IChannel nextSink = next.Reverse;

PooledBuffer buf = PooledBuffer.Rent(8);
Encoding.UTF8.GetBytes("abcdefgh", buf.Span);
await source.WriteAsync(buf, buf.Length);
buf.Dispose(); // caller link released

using ReadResult read = await sink.ReadAsync(4, ReadBlockingMode.WaitAll);
PooledBuffer.Slice slice = read.ToSlice(); // adds a link, no copy
await next.WriteAsync(slice);              // forward the same bytes
slice.Dispose();
// read.Dispose() drops its link; buffer returns to pool when all links are done
```

## Design highlights (why it is fast)

- **Zero-copy by default**: writes take existing pooled buffers; reads hand back spans into those buffers.
- **Coalescing only when needed**: multi-segment reads copy into a contiguous rental only when `WaitAll` demands it.
- **Explicit lifetime**: ref-counted buffers and slices make ownership visible; disposal drives pool returns.
- **Convenient spans**: implicit casts avoid helper allocations while keeping APIs span-friendly.

Build pipelines that pass buffers around; the pool—and a few well-placed disposals—do the rest.
