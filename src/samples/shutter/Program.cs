using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using NReco.Logging.File;
using Nethermind.Libp2p.Protocols;

using Nethermind.Libp2p.Core.Dto;
using Google.Protobuf;

ServiceProvider serviceProvider = new ServiceCollection()
    .AddLibp2p(builder => builder)
    .AddSingleton(new IdentifyProtocolSettings
    {
        ProtocolVersion = "/shutter/0.1.0",
        AgentVersion = "github.com/shutter-network/rolling-shutter/rolling-shutter"
    })
    .AddLogging(builder =>
            builder.SetMinimumLevel(LogLevel.Trace)
            .AddFile("/home/marc/shutter.log", append: true)
            .AddSimpleConsole(l =>
            {
                l.SingleLine = true;
                l.TimestampFormat = "[HH:mm:ss.FFF]";
            }))
    .BuildServiceProvider();

IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

// ILogger logger = serviceProvider.GetService<ILoggerFactory>()!.CreateLogger("Pubsub Chat");
CancellationTokenSource ts = new();

ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/23102");

Console.WriteLine(peer.Address);

PubsubRouter router = serviceProvider.GetService<PubsubRouter>()!;

ITopic topic = router.Subscribe("decryptionKeys");
topic.OnMessage += (byte[] msg) =>
{
    // Console.WriteLine("rec: " + msg);
    // Envelope envelope = Envelope.Parser.ParseFrom(msg);
    // DecryptionKeys decryptionKeys = DecryptionKeys.Parser.ParseFrom(envelope.Message.ToByteString());
    // Console.WriteLine(decryptionKeys.Eon);
    // Console.WriteLine(decryptionKeys.Gnosis.Slot);

    // foreach (Key key in decryptionKeys.Keys.AsEnumerable())
    // {
    //     Console.WriteLine(key.Key_.ToString());
    // }
};

MyProto proto = new();

_ = router.RunAsync(peer, proto, token: ts.Token);

// Add Peers
proto.OnAddPeer?.Invoke(["/ip4/161.35.71.168/tcp/23000/p2p/12D3KooWPjX9v7FWmPvAUSTMpG7j2jXWxNnUxyDZrXPqmK29QNEd"]);
proto.OnAddPeer?.Invoke(["/ip4/161.35.71.168/tcp/23001/p2p/12D3KooWJGopzaxs5G2FCPPMAy9zUoEdp5X7VebANvHGwiKW61Ck"]);
proto.OnAddPeer?.Invoke(["/ip4/161.35.71.168/tcp/23002/p2p/12D3KooWPV5c2K5oHugFFArAwaJfxUrrTEjjwJgCQ44LNTsi7p68"]);
proto.OnAddPeer?.Invoke(["/ip4/161.35.71.168/tcp/23003/p2p/12D3KooWKZBm2JvZE9SyPy3bQupgxtHzJC4y3E7xaHX5aRVewspC"]);

// proto.OnRemovePeer += (Multiaddress[] p) => {
//     Console.WriteLine("peer removed!");
//     return false;
// };

Console.ReadLine();
Console.WriteLine("Finished");

internal class MyProto : IDiscoveryProtocol
{
    public Func<Multiaddress[], bool>? OnAddPeer { get; set; }
    public Func<Multiaddress[], bool>? OnRemovePeer { get; set; }

    public Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
    {
        return Task.Delay(int.MaxValue);
    }
}
