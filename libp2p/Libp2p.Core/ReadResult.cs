// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace Nethermind.Libp2p.Core;

public readonly struct ReadResult
{
    public static ReadResult Ended = new() { Result = IOResult.Ended };
    public static ReadResult Cancelled = new() { Result = IOResult.Cancelled };

    public static ReadResult Empty = new() { Result = IOResult.Ok, Data = new ReadOnlySequence<byte>() };
    public IOResult Result { get; init; }
    public ReadOnlySequence<byte> Data { get; init; }

    internal static ReadResult Ok(ReadOnlySequence<byte> data) => new() { Result = IOResult.Ok, Data = data };
}

