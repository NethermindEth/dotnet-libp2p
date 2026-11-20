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

    /// <summary>Retains a slice for forwarding beyond this result's lifetime.</summary>
    public Channels.PooledBuffer.Slice ToSlice() => _owned.ToSlice();

    public void Dispose() => _owned.Dispose();

    private ReadResult(Channels.ReadResult result)
    {
        Result = (IOResult)result.Result;
        _owned = result;
    }

    internal static ReadResult FromOwned(Channels.ReadResult result) => new(result);

    internal static ReadResult Ok(ReadOnlySequence<byte> data) => new() { Result = IOResult.Ok, Data = data };
}
