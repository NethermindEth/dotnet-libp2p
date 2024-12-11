using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Stack;
using System.Text;

namespace Libp2p.E2eTests;

public class E2eTestSetup : IDisposable
{
    private readonly CancellationTokenSource _commonTokenSource = new();
    public void Dispose()
    {
        _commonTokenSource.Cancel();
        _commonTokenSource.Dispose();
    }

    protected CancellationToken Token => _commonTokenSource.Token;

    protected static TestContextLoggerFactory loggerFactory = new();
    private int _peerCounter = 0;

    protected ILogger TestLogger { get; set; } = loggerFactory.CreateLogger("test-setup");

    public Dictionary<int, IPeer> Peers { get; } = [];
    public Dictionary<int, PeerStore> PeerStores { get; } = [];
    public Dictionary<int, ServiceProvider> ServiceProviders { get; } = [];

    protected virtual IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
    {
        return builder.AddAppLayerProtocol<IncrementNumberTestProtocol>();
    }

    protected virtual IServiceCollection ConfigureServices(IServiceCollection col)
    {
        return col;
    }

    protected virtual void AddToPrintState(StringBuilder sb, int index)
    {
    }

    protected virtual void AddAt(int index)
    {

    }

    public async Task AddPeersAsync(int count)
    {
        int totalCount = _peerCounter + count;

        for (; _peerCounter < totalCount; _peerCounter++)
        {
            // But we create a seprate setup for every peer
            ServiceProvider sp = ServiceProviders[_peerCounter] =
                ConfigureServices(
                    new ServiceCollection()
                       .AddLibp2p(ConfigureLibp2p)
                       .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
                )
                   .BuildServiceProvider();

            PeerStores[_peerCounter] = ServiceProviders[_peerCounter].GetService<PeerStore>()!;
            Peers[_peerCounter] = sp.GetService<IPeerFactory>()!.Create(TestPeers.Identity(_peerCounter));

            await Peers[_peerCounter].StartListenAsync(token: Token);

            AddAt(_peerCounter);
        }
    }


    private int stateCounter = 1;

    public void PrintState(bool outputToConsole = false)
    {
        StringBuilder reportBuilder = new();
        reportBuilder.AppendLine($"Test state#{stateCounter++}");

        foreach ((int index, IPeer peer) in Peers)
        {
            AddToPrintState(reportBuilder, index);
            reportBuilder.AppendLine(peer.ToString());
            reportBuilder.AppendLine();
        }

        string report = reportBuilder.ToString();

        if (outputToConsole)
        {
            Console.WriteLine(report);
        }
        else
        {
            TestLogger.LogInformation(report.ToString());
        }
    }
}
