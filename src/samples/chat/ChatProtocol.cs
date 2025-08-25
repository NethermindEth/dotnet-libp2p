// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

internal class ChatProtocol : SymmetricSessionProtocol, ISessionProtocol
{
    private static readonly ConsoleReader ConsoleReader = new();
    private readonly ConsoleColor defaultConsoleColor = Console.ForegroundColor;

    public string Id => "/chat/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, ISessionContext context, bool isListener)
    {
        Console.Write("> ");
        _ = Task.Run(async () =>
        {
            for (; ; )
            {
                string aLine = await channel.ReadLineAsync();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("{0}", aLine);
                Console.ForegroundColor = defaultConsoleColor;
                Console.Write("> ");
            }
        });
        for (; ; )
        {
            string line = await ConsoleReader.ReadLineAsync();
            if (line == "exit")
            {
                return;
            }
            Console.Write("> ");
            await channel.WriteLineAsync(line);
        }
    }
}
