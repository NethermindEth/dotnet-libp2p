// See https://aka.ms/new-console-template for more information

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Core.Discovery;

ServiceProvider serviceProvider = new ServiceCollection()
    .AddLibp2p(builder => builder
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

Identity localPeerIdentity = new();
string addr = $"/ip4/0.0.0.0/tcp/0/p2p/{localPeerIdentity.PeerId}";

ILocalPeer peer = peerFactory.Create(localPeerIdentity, addr);

PubsubRouter router = serviceProvider.GetService<PubsubRouter>()!;
string forkDigest = "ee7b3a32";
ITopic topic = router.Subscribe($"/eth2/{forkDigest}/beacon_block/ssz_snappy");
ITopic topic1 = router.Subscribe($"/eth2/{forkDigest}/light_client_optimistic_update/ssz_snappy");
ITopic topic2 = router.Subscribe($"/eth2/{forkDigest}/light_client_finality_update/ssz_snappy");
topic.OnMessage += (byte[] msg) =>
{

};
topic1.OnMessage += (byte[] msg) =>
{

};
topic2.OnMessage += (byte[] msg) =>
{

};

var d0 = new ManualDiscoveryProtocol();

_ = router.RunAsync(peer, d0, token: ts.Token);


await Task.Delay(1000);
d0.Add("");


Console.ReadKey();
ts.Cancel();

record class ChatMessage(string Message, string SenderId, string SenderNick);


class ManualDiscoveryProtocol : IDiscoveryProtocol {
    public void Add(string peerId){
        OnAddPeer?.Invoke([new(peerId)]);
    }

    public async Task DiscoverAsync(Multiaddr localPeerAddr, CancellationToken token = default){}

    public Func<Multiaddr[], bool>? OnAddPeer { get; set; }
    public Func<Multiaddr[], bool>? OnRemovePeer { get; set; }
}
