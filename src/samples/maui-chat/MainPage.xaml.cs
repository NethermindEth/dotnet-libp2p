// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;

namespace MauiChat;

public partial class MainPage : ContentPage
{
    ChatProtocol? chatProtocol;

    public MainPage()
    {
        InitializeComponent();

        _ = Task.Run(async () =>
        {
            try
            {
                chatProtocol = new ChatProtocol() { OnServerMessage = (msg) => AddLine("AI", msg) };

                ServiceProvider serviceProvider = new ServiceCollection()
                    .AddLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddDebug();   // logs to platform debug output
                        logging.AddConsole(); // works on Windows/macOS
                    })
                    .AddLibp2p(builder => ((Libp2pPeerFactoryBuilder)builder).WithQuic().AddProtocol(chatProtocol))
                    .BuildServiceProvider();

                IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;

                CancellationTokenSource ts = new();

                Multiaddress remoteAddr = "/ip4/139.177.181.61/tcp/42000/p2p/12D3KooWBXu3uGPMkjjxViK6autSnFH5QaKJgTwW8CaSxYSD6yYL";
                //Multiaddress remoteAddr = "/ip4/139.177.181.61/udp/42000/quic-v1/p2p/12D3KooWBXu3uGPMkjjxViK6autSnFH5QaKJgTwW8CaSxYSD6yYL";

                await using ILocalPeer localPeer = peerFactory.Create();

                ISession remotePeer = await localPeer.DialAsync(remoteAddr, ts.Token);

                await remotePeer.DialAsync<ChatProtocol>(ts.Token);

                AddLine("System", "Connected");
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

    private void Button_Clicked(object sender, EventArgs e)
    {
        chatProtocol?.OnClientMessage?.Invoke(Msg.Text);
        AddLine("me", Msg.Text);
        Msg.Text = "";
        Msg.Focus();
    }
}
