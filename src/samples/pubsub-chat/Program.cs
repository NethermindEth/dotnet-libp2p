// See https://aka.ms/new-console-template for more information

using Libp2p.Protocols.Floodsub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Builder;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using System.Text;
using System.Text.Json;

ServiceProvider serviceProvider = new ServiceCollection()
    .AddLibp2p(builder => builder
        .WithPlaintextEnforced()
        .AddAppLayerProtocol<ChatProtocol>()
        )
    .AddLogging(builder =>
        builder.SetMinimumLevel(args.Contains("--trace") ? LogLevel.Trace : LogLevel.Debug)
            .AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.FFF]";
            }))
    .BuildServiceProvider();

IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

ILogger logger = serviceProvider.GetService<ILoggerFactory>()!.CreateLogger("Pubsub Chat");
CancellationTokenSource ts = new();

Random r = new();
byte[] buf = new byte[32];
r.NextBytes(buf);
Identity optionalFixedIdentity = Identity.FromPrivateKey(buf);
string addr = $"/ip4/0.0.0.0/tcp/0/p2p/{optionalFixedIdentity.PeerId}";

ILocalPeer peer = peerFactory.Create(optionalFixedIdentity, addr);


PubsubRouter router = serviceProvider.GetService<PubsubRouter>()!;

ITopic topic = router.Subscribe("chat-room:awesome-chat-room");

_ = router.RunAsync(peer, new MDnsDiscoveryProtocol(serviceProvider.GetService<ILoggerFactory>()), ts.Token);


topic.OnMessage += ((byte[] msg) =>
{
    ChatMessage? chatMessage = JsonSerializer.Deserialize<ChatMessage>(Encoding.UTF8.GetString(msg));
    if (chatMessage is not null)
    {
        Console.WriteLine("{0}: {1}", chatMessage.SenderNick, chatMessage.Message);
    }
});

string peerId = peer.Address.At(Nethermind.Libp2p.Core.Enums.Multiaddr.P2p)!;

string nickName = "libp2p-dotnet";

while (true)
{
    string msg = Console.ReadLine()!;
    if (msg == "exit")
    {
        break;
    }
    if (string.IsNullOrWhiteSpace(msg))
    {
        continue;
    }
    topic.Publish(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ChatMessage(msg, peerId, nickName))));
}

ts.Cancel();

record class ChatMessage(string Message, string SenderId, string SenderNick);
