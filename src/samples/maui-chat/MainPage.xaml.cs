// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
#if ANDROID
using System.Runtime.InteropServices;
#endif

namespace MauiChat;

internal sealed class UiLogProvider(Action<string, string> addLine) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new UiLogger(addLine);

    public void Dispose()
    {
    }

    private sealed class UiLogger(Action<string, string> addLine) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            addLine(logLevel.ToString(), message);
        }
    }
}

public partial class MainPage : ContentPage
{
    private static readonly Multiaddress[] RemoteAddresses =
    [
        "/ip4/139.177.181.61/tcp/42000/p2p/12D3KooWBXu3uGPMkjjxViK6autSnFH5QaKJgTwW8CaSxYSD6yYL",
        "/ip4/139.177.181.61/udp/42000/quic-v1/p2p/12D3KooWBXu3uGPMkjjxViK6autSnFH5QaKJgTwW8CaSxYSD6yYL",
    ];

    private ChatProtocol? _chatProtocol;

#if ANDROID
    private static IntPtr sodiumHandle;
    private static bool sodiumResolverConfigured;
#endif

    public MainPage()
    {
        InitializeComponent();
        _ = RunChatAsync();
    }

    private async Task RunChatAsync()
    {
        try
        {
#if ANDROID
            ConfigureNoiseNativeLibrary();
#endif
            _chatProtocol = new ChatProtocol { OnServerMessage = msg => AddLine("AI", msg) };

            using ServiceProvider serviceProvider = CreateServiceProvider(_chatProtocol);
            IPeerFactory peerFactory = serviceProvider.GetRequiredService<IPeerFactory>();

            using CancellationTokenSource cancellation = new();
            await using ILocalPeer localPeer = peerFactory.Create();

            ISession remotePeer = await localPeer.DialAsync(RemoteAddresses, cancellation.Token);
            AddLine("System", $"Connected via {remotePeer.RemoteAddress}");

            _ = OpenChatProtocolAsync(remotePeer, cancellation.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AddLine("System", $"Problem, {ex}");
        }
    }

    private ServiceProvider CreateServiceProvider(ChatProtocol chatProtocol)
    {
        return new ServiceCollection()
            .AddLogging(ConfigureLogging)
            .AddSingleton<IProtocolStackSettings, ProtocolStackSettings>()
            .AddSingleton<PeerStore>()
            .AddSingleton<MultiplexerSettings>()
            .AddSingleton<IPeerFactoryBuilder>(sp => new MauiChatPeerFactoryBuilder(sp).WithQuic().AddProtocol(chatProtocol))
            .AddSingleton(sp => sp.GetRequiredService<IPeerFactoryBuilder>().Build())
            .BuildServiceProvider();
    }

    private void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddDebug();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Warning);
        logging.AddProvider(new UiLogProvider(AddLine));
    }

    private async Task OpenChatProtocolAsync(ISession remotePeer, CancellationToken cancellationToken)
    {
        try
        {
            await remotePeer.DialAsync<ChatProtocol>(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AddLine("System", $"Problem, {ex}");
        }
    }

#if ANDROID
    private static void ConfigureNoiseNativeLibrary()
    {
        if (sodiumResolverConfigured)
        {
            return;
        }

        string nativeLibraryDir = Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir
            ?? throw new DirectoryNotFoundException("Android native library directory was not found.");
        string sodiumPath = Directory.EnumerateFiles(nativeLibraryDir, "libsodium.so", SearchOption.AllDirectories)
            .FirstOrDefault()
            ?? throw new FileNotFoundException("libsodium.so was not found in the app native library directory.", nativeLibraryDir);

        sodiumHandle = NativeLibrary.Load(sodiumPath);
        NativeLibrary.SetDllImportResolver(
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
                Text = msg + Environment.NewLine,
            });
        });
    }

    private async void Button_Clicked(object sender, EventArgs e)
    {
        await SendMessageAsync();
    }

    private async Task SendMessageAsync()
    {
        string msg = Msg.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(msg))
        {
            return;
        }

        Func<string, Task>? send = _chatProtocol?.OnClientMessage;
        if (send is null)
        {
            AddLine("System", "Problem, chat protocol is not connected.");
            return;
        }

        try
        {
            await send(msg);
            AddLine("me", msg);
            Msg.Text = string.Empty;
            Msg.Focus();
        }
        catch (Exception ex)
        {
            AddLine("System", $"Problem, failed to send message: {ex.Message}");
        }
    }
}
