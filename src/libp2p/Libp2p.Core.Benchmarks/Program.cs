// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using System;
using Nethermind.Libp2p.Core;
using System.Buffers;
using System.Diagnostics;

Channel chan = new();
IChannel revChan = ((Channel)chan).Reverse;

const long GiB = 1024 * 1024 * 1024;
long PacketSize = 1 * 1024;
long TotalSize = 1024 * 1024 * 1024;

_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            byte[] array = new byte[PacketSize];
            await chan.WriteAsync(new ReadOnlySequence<byte>(array.AsMemory()));
        }
        catch
        {

        }
    }

});

await Task.Run(async () =>
{
    Stopwatch s = Stopwatch.StartNew();
    ReadOnlySequence<byte> d;

    long j = 0;
    long i = 0;
    while (i < TotalSize)
    {
        try
        {
            d = (await revChan.ReadAsync(0, ReadBlockingMode.WaitAny));
            i += d.Length;
        }
        catch
        {

        }
    }
    j++;

    Console.WriteLine(((double)TotalSize) / GiB / s.Elapsed.TotalMilliseconds * 1e3);
});

