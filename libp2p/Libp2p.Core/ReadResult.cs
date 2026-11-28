// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Libp2p.Core;

public readonly struct ReadResult : IDisposable
{
    private readonly Channels.ReadResult _owned;
    public static ReadResult Ended = new() { Result = IOResult.Ended };
    public static ReadResult Cancelled = new() { Result = IOResult.Cancelled };
    public static ReadResult Aborted = new() { Result = IOResult.Aborted };

    public static ReadResult Empty = new() { Result = IOResult.Ok, Data = new ReadOnlySequence<byte>() };
    public IOResult Result { get; init; }
    public ReadOnlySequence<byte> Data
    {
        get => _owned.Sequence;
        init => _owned = Channels.ReadResult.Ok(value);
    }

    /// <summary>Whether this result retains pooled storage that requires disposal.</summary>
    public bool IsOwned => _owned.IsOwned;

    /// <summary>Retains an independent slice of an owned read.</summary>
    /// <exception cref="InvalidOperationException">The read uses borrowed storage.</exception>
    public Channels.PooledBuffer.Slice ToSlice() => _owned.ToSlice();

    /// <summary>Forwards the data as one write. The caller can dispose this read as soon as the method returns.</summary>
    /// <remarks>The writer retains pooled data or receives a stable copy. Borrowed data keeps its original segments.</remarks>
    /// <returns>The destination's write status, or an unsuccessful read's status without writing to the destination.</returns>
    public ValueTask<IOResult> WriteToAsync(IWriter writer, CancellationToken token = default)
    {
        if (Result != IOResult.Ok) return ValueTask.FromResult(Result);
        ArgumentNullException.ThrowIfNull(writer);
        return _owned.Owner is { } slice ? writer.WriteAsync(slice, token) : writer.WriteAsync(Data, token);
    }

    public void Dispose() => _owned.Dispose();

    private ReadResult(Channels.ReadResult result)
    {
        Result = (IOResult)result.Result;
        _owned = result;
    }

    internal static ReadResult FromOwned(Channels.ReadResult result) => new(result);

    /// <summary>Creates a successful read over stable borrowed data.</summary>
    public static ReadResult Ok(ReadOnlySequence<byte> data) => new() { Result = IOResult.Ok, Data = data };

    /// <summary>Creates a successful read that takes ownership of the supplied slice.</summary>
    /// <remarks>Dispose the result to release the slice. A reader can pass a fresh slice such as <c>buffer[..length]</c>.</remarks>
    public static ReadResult FromOwnedSlice(Channels.PooledBuffer.Slice slice) => new(Channels.ReadResult.FromOwnedSlice(slice));
}
