// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Nethermind.Libp2p;
using PubsubChat;

Regex omittedLogs = new(".*(MDnsDiscoveryProtocol|IpTcpProtocol).*");

bool headless = args.Contains("--headless");

var services = new ServiceCollection()
    .AddLibp2p(builder => builder.WithPubsub())
    .AddLogging(builder =>
    {
        builder.SetMinimumLevel(args.Contains("--trace") ? LogLevel.Trace : LogLevel.Information)
               .AddFilter((_, type, lvl) => !omittedLogs.IsMatch(type!));

        if (headless)
        {
            builder.AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.fff]";
            });
        }
    })
    .BuildServiceProvider();

var chatService = new ChatService(services);
await chatService.StartAsync();

string nickName = "libp2p-dotnet";

if (!headless)
{
    Gui.RunGui(chatService, nickName);
}
else
{
    while (true)
    {
        string? msg = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(msg))
            continue;

        if (msg == "exit")
            break;

        chatService.Publish(msg, nickName);
    }
    chatService.Stop();
}
