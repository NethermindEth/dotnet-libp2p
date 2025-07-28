// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using PubsubChat;
using Terminal.Gui;
using Timer = System.Threading.Timer;

public static class Gui
{
    public static void RunGui(ChatService chatService, string nickName)
    {
        Application.Init();
        var top = Application.Top;

        var statusBar = new StatusBar(new StatusItem[] {
            new StatusItem(Key.Null, $"Peer ID: {chatService.LocalPeerId}", null),
            new StatusItem(Key.F1, "~F1~ Help", () => {
                MessageBox.Query("Help", "F1: Help\nF2: Messages\nF3: Logs\nF4: Peers\nEsc: Quit", "OK");
            }),
            new StatusItem(Key.Esc, "~Esc~ Quit", () => { Application.RequestStop(); }),
        });
        top.Add(statusBar);

        var chatWindow = new Window("libp2p Chat")
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 1
        };
        top.Add(chatWindow);

        var tabView = new TabView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 3
        };
        chatWindow.Add(tabView);

        var messagesView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true
        };
        var messagesTab = new TabView.Tab("Messages", messagesView);
        tabView.AddTab(messagesTab, true);
        var logsView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true
        };
        var logsTab = new TabView.Tab("Logs", logsView);
        tabView.AddTab(logsTab, false);

        var peersView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true
        };
        var peersTab = new TabView.Tab("Peers", peersView);
        tabView.AddTab(peersTab, false);

        tabView.SelectedTab = messagesTab;

        chatWindow.KeyPress += (args) => {
            if (args.KeyEvent.Key == Key.F2) {
                tabView.SelectedTab = messagesTab;
                args.Handled = true;
            } else if (args.KeyEvent.Key == Key.F3) {
                tabView.SelectedTab = logsTab;
                args.Handled = true;
            } else if (args.KeyEvent.Key == Key.F4) {
                tabView.SelectedTab = peersTab;
                args.Handled = true;
            } else if (args.KeyEvent.Key == Key.Esc) {
                Application.RequestStop();
                args.Handled = true;
            }
        };

        var inputLabel = new Label("Message: ")
        {
            X = 0,
            Y = Pos.Bottom(tabView)
        };
        chatWindow.Add(inputLabel);

        var inputField = new TextField("")
        {
            X = Pos.Right(inputLabel),
            Y = Pos.Bottom(tabView),
            Width = Dim.Fill() - inputLabel.Text.Length
        };
        chatWindow.Add(inputField);

        inputField.KeyPress += (args) =>
        {
            if (args.KeyEvent.Key == Key.Enter)
            {
                string msg = inputField.Text?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    chatService.Publish(msg, nickName);
                    inputField.Text = "";
                }
                args.Handled = true;
            }
        };

        var updateTimer = new Timer((state) =>
        {
            Application.MainLoop.Invoke(() =>
            {
                lock (chatService.Messages)
                {
                    messagesView.Text = string.Join("\n", chatService.Messages);
                    if (tabView.SelectedTab == messagesTab)
                    {
                        if (messagesView.Lines > 0)
                        {
                            messagesView.CursorPosition = new Point(0, Math.Max(0, messagesView.Lines - 1));
                        }
                    }
                }

                lock (chatService.Logs)
                {
                    logsView.Text = string.Join("\n", chatService.Logs);
                    if (tabView.SelectedTab == logsTab)
                    {
                        if (logsView.Lines > 0)
                        {
                            logsView.CursorPosition = new Point(0, Math.Max(0, logsView.Lines - 1));
                        }
                    }
                }

                string peersText = $"Connected Peers: {chatService.ConnectedPeers.Count}\n\n";

                if (chatService.ConnectedPeers.Count > 0)
                {
                    foreach (var peer in chatService.ConnectedPeers)
                    {
                        peersText += $"Peer ID: {peer.Value.PeerId}\n";
                        peersText += $"Address: {peer.Value.Address}\n";
                        peersText += $"Connected: {peer.Value.ConnectedAt:HH:mm:ss}\n";
                        peersText += $"User Agent: {peer.Value.UserAgent}\n";
                        peersText += "----------------------------\n";
                    }
                }
                else
                {
                    peersText += "No peers connected yet.";
                }

                peersView.Text = peersText;
            });
        }, null, 0, 1000);

        Application.Run();
        chatService.Stop();
    }
}
