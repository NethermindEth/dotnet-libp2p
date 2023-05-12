// See https://aka.ms/new-console-template for more information
Console.WriteLine("Hello, World!");

class Protocol
{
    public void Listen()
    {
        var peerId = "";
        Router.Instance.Dialed("peerId", true, () => { });
        while (true)
        {
            var rpc = Read();
            Router.Instance.Handle(peerId, rpc);
        }
    }
    public void Dial(string addr)
    {
        Router.Instance.Dialed("peerId", false, () => { });
        Send(Router.Instance.Mesh.Keys.ToArray());
    }

    private void Send(object any) { }
    private dynamic Read()
    {
        return "";
    }
}


class Router
{
    public static Router Instance = new Router(new Discovery(), new Protocol());

    public Router(Discovery disc, Protocol protocol)
    {
        disc.OnNewPeer += (string addr) =>
        {
            var peerId = addr.ToPeerId();
            if (!Peers.ContainsKey(peerId))
            {
                Peers[peerId] = new PeerState() { IsDialing = true };
                protocol.Dial(addr);
            }
        };
    }

    public struct PeerState
    {
        public bool IsDialing { get; set; }
        public bool Outbound { get; set; }
    }

    public Dictionary<string, PeerState> Peers = new();
    public Dictionary<string, HashSet<string>> Mesh = new();
    public Dictionary<string, HashSet<string>> Fanout = new();

    internal void Dialed(string peerId, bool inBound, Action value)
    {
        if (!Peers.ContainsKey(peerId))
        {
            Peers[peerId] = new PeerState();

        }
    }

    internal void Handle(string peerId, dynamic rpc)
    {
        foreach (var sub in rpc.Subscribe)
        {
            Fanout[sub].Add(peerId);
        }
    }
}

class Discovery
{
    public delegate void OnNewPeerEvent(string addr);

    public event OnNewPeerEvent? OnNewPeer;
}

public static class Exts
{
    public static string ToPeerId(this string addr) => "peerId-of-" + addr;
}
