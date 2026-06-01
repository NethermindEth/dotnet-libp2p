// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;

TaskCompletionSource<string> firstReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
var chatProtocol = new ChatProtocol
{
    OnServerMessage = msg =>
    {
        Console.WriteLine("AI: {0}", msg);
        firstReply.TrySetResult(msg);
    }
};

ServiceProvider serviceProvider = new ServiceCollection()
    .AddLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "[HH:mm:ss.FFF]";
        });
        logging.SetMinimumLevel(args.Contains("--trace") ? LogLevel.Trace : LogLevel.Information);
    })
    .AddLibp2p(builder => builder.WithQuic().AddProtocol(chatProtocol))
    .BuildServiceProvider();

IPeerFactory peerFactory = serviceProvider.GetRequiredService<IPeerFactory>();

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

const string TcpRemoteAddr = "/ip4/139.177.181.61/tcp/42000/p2p/12D3KooWBXu3uGPMkjjxViK6autSnFH5QaKJgTwW8CaSxYSD6yYL";
const string QuicRemoteAddr = "/ip4/139.177.181.61/udp/42000/quic-v1/p2p/12D3KooWBXu3uGPMkjjxViK6autSnFH5QaKJgTwW8CaSxYSD6yYL";

int messageIndex = Array.IndexOf(args, "--message");
string? singleMessage = messageIndex >= 0 && messageIndex < args.Length - 1
    ? string.Join(' ', args.Skip(messageIndex + 1))
    : null;

Multiaddress remoteAddr = args.FirstOrDefault(a => a.StartsWith('/')) ??
    (args.Contains("--quic") ? QuicRemoteAddr : TcpRemoteAddr);

await using ILocalPeer localPeer = peerFactory.Create();
ISession remotePeer = await localPeer.DialAsync(remoteAddr, cancellation.Token);

Task chatTask = remotePeer.DialAsync<ChatProtocol>(cancellation.Token);
Task readyTask = chatProtocol.Ready.Task.WaitAsync(cancellation.Token);
Task completed = await Task.WhenAny(chatTask, readyTask);
if (completed == chatTask)
{
    await chatTask;
    throw new InvalidOperationException("Chat protocol closed before it became ready.");
}

await readyTask;
Func<string, Task<IOResult>> sendMessage = chatProtocol.OnClientMessage ??
    throw new InvalidOperationException("Chat protocol became ready without a send delegate.");

Console.WriteLine("System: Connected via {0}", remotePeer.RemoteAddress);

if (singleMessage is not null)
{
    await SendMessageAsync(sendMessage, singleMessage);
    await firstReply.Task.WaitAsync(TimeSpan.FromSeconds(120));
    await remotePeer.DisconnectAsync();
    return;
}

ConsoleReader reader = new();
while (!cancellation.IsCancellationRequested)
{
    string msg = await reader.ReadLineAsync(cancellation.Token);
    if (!string.IsNullOrWhiteSpace(msg))
    {
        await SendMessageAsync(sendMessage, msg);
    }
}

await chatTask;

static async Task SendMessageAsync(Func<string, Task<IOResult>> sendMessage, string message)
{
    if (await sendMessage(message) != IOResult.Ok)
    {
        throw new IOException("Failed to send chat message.");
    }
}
