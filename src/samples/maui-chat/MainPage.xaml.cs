// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;

namespace MauiChat;

public class Prov(Action<string, string> addLine) : ILoggerProvider
{
    private Action<string, string> addLine = addLine;

    public ILogger CreateLogger(string categoryName)
    {
        return new Log(addLine);
    }

    public void Dispose()
    {

    }

    public class Log(Action<string, string> addLine) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            addLine(logLevel.ToString(), state?.ToString() ?? string.Empty);
        }
    }
}
public partial class MainPage : ContentPage
{
    ChatProtocol? chatProtocol;
#if ANDROID
    private static IntPtr sodiumHandle;
    private static bool sodiumResolverConfigured;
#endif

    public MainPage()
    {
        InitializeComponent();

        _ = Task.Run(async () =>
        {
            try
            {
#if ANDROID
                ConfigureNoiseNativeLibrary();
#endif

                chatProtocol = new ChatProtocol() { OnServerMessage = (msg) => AddLine("AI", msg) };

                ServiceCollection services = new ServiceCollection();
                services
                    .AddLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddDebug();   // logs to platform debug output
                        logging.AddConsole(); // works on Windows/macOS
                        logging.SetMinimumLevel(LogLevel.Warning);
                        logging.AddProvider(new Prov(AddLine));
                    })
                    .AddSingleton<IProtocolStackSettings, ProtocolStackSettings>()
                    .AddSingleton<PeerStore>()
                    .AddSingleton<MultiplexerSettings>()
                    .AddSingleton<IPeerFactoryBuilder>(sp => new MauiChatPeerFactoryBuilder(sp).WithQuic().AddProtocol(chatProtocol))
                    .AddSingleton(sp => sp.GetRequiredService<IPeerFactoryBuilder>().Build());

                ServiceProvider serviceProvider = services.BuildServiceProvider();

                IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

                CancellationTokenSource ts = new();

                Multiaddress[] remoteAddrs = [
                    "/ip4/139.177.181.61/tcp/42000/p2p/12D3KooWBXu3uGPMkjjxViK6autSnFH5QaKJgTwW8CaSxYSD6yYL",
                    "/ip4/139.177.181.61/udp/42000/quic-v1/p2p/12D3KooWBXu3uGPMkjjxViK6autSnFH5QaKJgTwW8CaSxYSD6yYL",
                ];

                await using ILocalPeer localPeer = peerFactory.Create();

                ISession remotePeer = await localPeer.DialAsync(remoteAddrs, ts.Token);

                AddLine("System", $"Connected via {remotePeer.RemoteAddress}");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await remotePeer.DialAsync<ChatProtocol>(ts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception e)
                    {
                        AddLine("System", $"Problem, {e}");
                    }
                });
                await Task.Delay(-1, ts.Token);
            }
            catch (Exception e)
            {
                AddLine("System", $"Problem, {e}");
            }
            //{ server
            //Identity optionalFixedIdentity = new(Enumerable.Repeat((byte)42, 32).ToArray());
            //await using ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

            //string addrTemplate = true ?
            //    "/ip4/0.0.0.0/udp/{0}/quic-v1" :
            //    "/ip4/0.0.0.0/tcp/{0}";

            //peer.ListenAddresses.CollectionChanged += (_, args) =>
            //{
            //};


            //try
            //{
            //    await peer.StartListenAsync(
            //        [string.Format(addrTemplate, "0")],
            //        ts.Token);
            //}
            //catch (Exception ex)
            //{

            //}
            //}
        });
    }

#if ANDROID
    private static void ConfigureNoiseNativeLibrary()
    {
        if (sodiumResolverConfigured)
        {
            return;
        }

        string? nativeLibraryDir = Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir;
        string? sodiumPath = nativeLibraryDir is null
            ? null
            : System.IO.Directory.EnumerateFiles(nativeLibraryDir, "libsodium.so", System.IO.SearchOption.AllDirectories)
                .FirstOrDefault();
        if (sodiumPath is null)
        {
            throw new System.IO.FileNotFoundException("libsodium.so was not found in the app native library directory.", nativeLibraryDir);
        }

        sodiumHandle = System.Runtime.InteropServices.NativeLibrary.Load(sodiumPath);
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
            typeof(Noise.KeyPair).Assembly,
            (libraryName, _, _) => libraryName is "libsodium" or "sodium" ? sodiumHandle : IntPtr.Zero);
        sodiumResolverConfigured = true;
    }
#endif

    private void AddLine(string from, string msg)
    {
        Dispatcher.Dispatch(() =>
        {
            ChatContent.Spans.Add(new Span
            {
                Text = $"{from}: ",
                FontAttributes = FontAttributes.Bold,
            });

            ChatContent.Spans.Add(new Span
            {
                Text = msg + "\n",
            });
        });
    }

    private async void Button_Clicked(object sender, EventArgs e)
    {
        string msg = Msg.Text;
        if (string.IsNullOrWhiteSpace(msg))
        {
            return;
        }

        Func<string, Task>? send = chatProtocol?.OnClientMessage;
        if (send is null)
        {
            AddLine("System", "Problem, chat protocol is not connected.");
            return;
        }

        try
        {
            await send(msg);
            AddLine("me", msg);
            Msg.Text = "";
            Msg.Focus();
        }
        catch (Exception ex)
        {
            AddLine("System", $"Problem, failed to send message: {ex.Message}");
        }
    }
}
