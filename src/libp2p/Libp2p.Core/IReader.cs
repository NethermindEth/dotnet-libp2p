// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text;

namespace Nethermind.Libp2p.Core;

public interface IReader
{
    // async Task<string> ReadLineAsync(bool prependedWithSize = true)
    // {
    //     ulong size = await ReadVarintAsync();
    //     byte[] buf = new byte[size];
    //     await ReadAsync(size, );
    //     return Encoding.UTF8.GetString(buf).TrimEnd('\n');
    // }

    // Task<ulong> ReadVarintAsync()
    // {
    //     return VarInt.Decode(this);
    // }

    Task<ReadOnlySequence<byte>> ReadAsync(int length = 0, bool blocking = true, CancellationToken token = default);
}


// class X
// {
//     void P()
//     {
//         System.Threading.Channels.Channel<byte> c = System.Threading.Channels.Channel.CreateBounded<byte>(0);
//     }
// }