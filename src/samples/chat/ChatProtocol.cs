// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text;
using Nethermind.Libp2p.Core;

internal class ChatProtocol : SymmetricProtocol, IProtocol
{
    private static readonly ConsoleReader Reader = new();
    private readonly ConsoleColor defautConsoleColor = Console.ForegroundColor;

    public string Id => "/chat/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context, bool isListener)
    {
        Console.Write("> ");
        _ = Task.Run(async () =>
        {
            for (; ; )
            {
                ReadOnlySequence<byte> read = await channel.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("{0}", Encoding.UTF8.GetString(read).Replace("\r", "").Replace("\n\n", ""));
                Console.ForegroundColor = defautConsoleColor;
                Console.Write("> ");
            }
        });
        for (; ; )
        {
            string line = await Reader.ReadLineAsync();
            if (line == "exit")
            {
                return;
            }
            Console.Write("> ");
            byte[] buf = Encoding.UTF8.GetBytes(line + "\n\n");
            await channel.WriteAsync(new ReadOnlySequence<byte>(buf));
        }
    }
}
