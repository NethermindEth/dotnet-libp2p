// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text;
using Nethermind.Libp2p.Core;

internal class ChatProtocol : SymetricProtocol, IProtocol
{
    private static readonly ConsoleReader Reader = new();
    public string Id => "/chat/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context, bool isListener)
    {
        Console.Write("> ");
        _ = Task.Run(async () =>
        {
            while (!channel.Token.IsCancellationRequested)
            {
                ReadOnlySequence<byte> read = await channel.Reader.ReadAsync(0, ReadBlockingMode.WaitAny, channel.Token);
                Console.Write(Encoding.UTF8.GetString(read).Replace("\n\n", "\n> "));
            }
        }, channel.Token);
        while (!channel.Token.IsCancellationRequested)
        {
            string line = await Reader.ReadLineAsync(channel.Token);
            Console.Write("> ");
            byte[] buf = Encoding.UTF8.GetBytes(line + "\n\n");
            await channel.Writer.WriteAsync(new ReadOnlySequence<byte>(buf));
        }
    }
}
