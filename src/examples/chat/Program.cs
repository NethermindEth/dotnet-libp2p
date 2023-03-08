using Libp2p.Builder;
using Libp2p.Core;

IPeerFactory peerFactory = Libp2pPeerFactoryBuilder.Instance
    .AddAppLayerProtocol<ChatProtocol>()
    .Build();

CancellationTokenSource ts = new();

if (args.Length > 0 && args[0] == "-d")
{
    MultiAddr remoteAddr = args[1];
    ILocalPeer localPeer = peerFactory.Create();

    Console.WriteLine($"Dialing {remoteAddr}");
    IRemotePeer remotePeer = await localPeer.DialAsync(remoteAddr, ts.Token);

    await remotePeer.DialAsync<ChatProtocol>(ts.Token);
    await remotePeer.DisconectAsync();
}
else
{
    Identity optionalFixedIdentity = Identity.FromPrivateKey(Enumerable.Repeat((byte)42, 32).ToArray());
    ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

    IListener listener = await peer.ListenAsync(
        $"/ip4/0.0.0.0/tcp/{(args.Length > 0 && args[0] == "-sp" ? args[1] : "0")}/p2p/{peer.Identity.PeerId}",
        ts.Token);
    Console.WriteLine($"Listener started at {listener.Address}");
    listener.OnConnection += async remotePeer => Console.WriteLine($"A peer connected {remotePeer.Address}");
    Console.CancelKeyPress += delegate { listener.DisconectAsync(); };

    await listener;
}
