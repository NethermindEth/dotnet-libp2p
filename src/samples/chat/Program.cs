// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p;
using System.Threading.Tasks;

using var serviceScope = new ServiceCollection()
    .AddLibp2p(builder => builder.AddAppLayerProtocol<ChatProtocol>())
    .AddLogging(builder =>
        builder.SetMinimumLevel(LogLevel.Debug)
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "[HH:mm:ss.FFF]";
                options.IncludeScopes = true;
            }))
    .BuildServiceProvider()
    .CreateScope();

var serviceProvider = serviceScope.ServiceProvider;
using var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
ILogger logger = loggerFactory.CreateLogger("Chat");
IPeerFactory peerFactory = serviceProvider.GetRequiredService<IPeerFactory>();
using var cts = new CancellationTokenSource();

AppDomain.CurrentDomain.ProcessExit += (s, e) =>
{
    cts.Cancel();
    serviceScope.Dispose();
};

Console.CancelKeyPress += (s, e) =>
{
    logger.LogInformation("Cancellation requested via Ctrl+C");
    cts.Cancel();
    e.Cancel = true;
};

try
{
    if (args.Length > 0 && args[0] == "-d")
    {
        await RunDialerModeAsync(args, peerFactory, logger, cts.Token, cts);
    }
    else
    {
        await RunListenerModeAsync(args, peerFactory, logger, cts.Token, cts);
    }
}
finally
{
    cts.Cancel();
    serviceScope.Dispose();
}


static async Task RunDialerModeAsync(string[] args, IPeerFactory peerFactory, ILogger logger, CancellationToken cancellationToken, CancellationTokenSource cts)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: dotnet run -d <multiaddress>");
        return;
    }

    TerminalUI ui = new();
    var tuilogger = new TerminalUI.TUILogger(ui, "Chat");
    ui.Initialize();

    Multiaddress remoteAddress;
    try
    {
        remoteAddress = Multiaddress.Decode(args[1]);
    }
    catch (Exception ex)
    {
        ui.AddLogMessage($"Invalid address: {ex.Message}");
        return;
    }

    ui.AddLogMessage($"Connecting to {remoteAddress}...");
    var localPeer = peerFactory.Create();

    var chatProtocol = new ChatProtocol(logger);
    chatProtocol.SetUI(ui);
    ui.UpdateStatus(localPeer.Identity.PeerId.ToString(), "Connecting...");

    var connectionTask = Task.Run(async () =>
    {
        try
        {
            using var scope = logger.BeginScope("Connection attempt to {Address}", remoteAddress);
            ui.AddLogMessage($"Starting connection to {remoteAddress}...");

            ISession session = await localPeer.DialAsync(remoteAddress, cancellationToken).ConfigureAwait(false);
            ui.AddLogMessage($"Connection established with {session.RemoteAddress}");
            ui.AddPeer("Remote Peer", remoteAddress.ToString());

            ui.AddLogMessage("Negotiating chat protocol...");
            await session.DialAsync<ChatProtocol>(cancellationToken).ConfigureAwait(false);
            ui.AddLogMessage("Chat protocol established successfully");

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ui.AddLogMessage("Connection cancelled or disconnected.");
        }
        catch (Exception ex)
        {
            ui.AddLogMessage($"Connection error: {ex.Message}");
            ui.AddLogMessage($"Stack trace: {ex.StackTrace}");
            logger.LogError(ex, "Dialer failed");
        }
    });

    ui.ExitRequested += (_, _) =>
    {
        ui.AddLogMessage("Exit requested, cleaning up...");
        cts.Cancel();
        try
        {
            localPeer.DisconnectAsync().Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during disconnect");
        }
        ui.Shutdown();
    };

    ui.Run();
}


static async Task RunListenerModeAsync(string[] args, IPeerFactory peerFactory, ILogger logger, CancellationToken cancellationToken, CancellationTokenSource cts)
{
    TerminalUI ui = new();
    var tuilogger = new TerminalUI.TUILogger(ui, "Chat");
    ui.Initialize();

    string port = "0";
    if (args.Length >= 2 && args[0] == "-sp")
    {
        port = args[1];
    }

    bool useQuic = args.Contains("-quic");
    string addrTemplate = useQuic ?
        "/ip4/0.0.0.0/udp/{0}/quic-v1" :
        "/ip4/0.0.0.0/tcp/{0}";

    var chatProtocol = new ChatProtocol(logger);
    chatProtocol.SetUI(ui);

    var localPeer = peerFactory.Create(new Identity(Enumerable.Repeat((byte)42, 32).ToArray()));

    var listenerTask = Task.Run(async () =>
    {
        try
        {
            using var scope = logger.BeginScope("Listener mode");

            localPeer.OnConnected += session =>
            {
                ui.AddLogMessage($"New peer connection from {session.RemoteAddress}");
                ui.AddLogMessage($"Remote peer info: {session.RemoteAddress}");
                ui.AddPeer("Connected Peer", session.RemoteAddress.ToString());
                return Task.CompletedTask;
            };

            string listenAddress = string.Format(addrTemplate, port);
            ui.AddLogMessage($"Starting listener on {listenAddress}...");
            await localPeer.StartListenAsync([listenAddress], cancellationToken).ConfigureAwait(false);

            ui.UpdateStatus(localPeer.Identity.PeerId.ToString(), string.Join(", ", localPeer.ListenAddresses));
            ui.AddLogMessage($"Listening active on {string.Join(", ", localPeer.ListenAddresses)}");
            ui.AddLogMessage($"Your Peer ID is: {localPeer.Identity.PeerId}");
            ui.AddLogMessage("\nPeers can connect using:");
            ui.AddLogMessage($"dotnet run -d {localPeer.ListenAddresses.First()}/p2p/{localPeer.Identity.PeerId}");

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ui.AddLogMessage("Listener shutting down...");
        }
        catch (Exception ex)
        {
            ui.AddLogMessage($"Listener error: {ex.Message}");
            ui.AddLogMessage($"Stack trace: {ex.StackTrace}");
            logger.LogError(ex, "Listener failed");
        }
    });

    ui.ExitRequested += (_, _) =>
    {
        ui.AddLogMessage("Exit requested, stopping listener...");
        cts.Cancel();
        try
        {
            localPeer.DisconnectAsync().Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during disconnect");
        }
        ui.Shutdown();
    };

    ui.Run();
}
