// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using PubsubChat;
using Timer = System.Threading.Timer;

public static class Gui
{
    private static bool _autoScrollMessages = true;
    private static bool _autoScrollLogs = true;
    private static bool _userHasScrolledMessages = false;
    private static bool _userHasScrolledLogs = false;

    // UI elements
    private static Window? _mainWindow;
    private static FrameView? _infoFrame;
    private static Label? _peerIdLabel;
    private static Label? _multiAddrLabel;
    private static TabView? _tabView;
    private static TextView? _messagesTextView;
    private static TextView? _logsTextView;
    private static TextView? _peersTextView;
    private static TextField? _inputField;
    private static Button? _sendButton;
    private static Button? _exitButton;
    private static Timer? _updateTimer;

    // Store tab references for easy access
    private static TabView.Tab? _messagesTab;
    private static TabView.Tab? _logsTab;
    private static TabView.Tab? _peersTab;

    public static void RunGui(ChatService chatService, string nickName)
    {
        try
        {
            Application.Init();

            var top = Application.Top;

            // Status bar
            var statusBar = new StatusBar(new StatusItem[] {
                new StatusItem(Key.F1, "~F1~ Help", OnHelp),
                new StatusItem(Key.F2, "~F2~ Messages", () => SwitchToTab(0)),
                new StatusItem(Key.F3, "~F3~ Logs", () => SwitchToTab(1)),
                new StatusItem(Key.F4, "~F4~ Peers", () => SwitchToTab(2)),
                new StatusItem(Key.Esc, "~Esc~ Quit", () => { Application.RequestStop(); }),
            });
            top.Add(statusBar);

            _mainWindow = new Window("libp2p Chat")
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 1
            };

            _infoFrame = new FrameView("Peer Info")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 3
            };

            _peerIdLabel = new Label($"Peer ID: {chatService.LocalPeerId}")
            {
                X = 1,
                Y = 0
            };

            _multiAddrLabel = new Label("Multiaddr: [Starting...]")
            {
                X = 1,
                Y = 1
            };

            _infoFrame.Add(_peerIdLabel, _multiAddrLabel);

            _tabView = new TabView()
            {
                X = 0,
                Y = Pos.Bottom(_infoFrame),
                Width = Dim.Fill(),
                Height = Dim.Fill(3)
            };

            // Messages tab
            _messagesTextView = new TextView()
            {
                ReadOnly = true,
                WordWrap = true,
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };

            _messagesTextView.KeyPress += OnMessagesKeyPress;

            // Logs tab
            _logsTextView = new TextView()
            {
                ReadOnly = true,
                WordWrap = true,
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };

            _logsTextView.KeyPress += OnLogsKeyPress;

            // Peers tab
            _peersTextView = new TextView()
            {
                ReadOnly = true,
                WordWrap = true,
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };

            _messagesTab = new TabView.Tab("Messages", _messagesTextView);
            _logsTab = new TabView.Tab("Logs", _logsTextView);
            _peersTab = new TabView.Tab("Peers", _peersTextView);

            _tabView.AddTab(_messagesTab, true);
            _tabView.AddTab(_logsTab, false);
            _tabView.AddTab(_peersTab, false);

            _inputField = new TextField("")
            {
                X = 0,
                Y = Pos.Bottom(_tabView),
                Width = Dim.Fill(12)
            };

            _inputField.CanFocus = true;
            _inputField.KeyPress += OnInputFieldKeyPress;

            _sendButton = new Button("Send")
            {
                X = Pos.Right(_inputField) + 1,
                Y = Pos.Top(_inputField)
            };

            _exitButton = new Button("Exit")
            {
                X = Pos.Right(_sendButton) + 1,
                Y = Pos.Top(_inputField)
            };

            _sendButton.Clicked += () => OnSendButtonClicked(chatService, nickName);
            _exitButton.Clicked += OnExitButtonClicked;

            _mainWindow.Add(_infoFrame, _tabView, _inputField, _sendButton, _exitButton);
            top.Add(_mainWindow);

            top.KeyPress += OnGlobalKeyPress;

            _inputField.SetFocus();

            // Start the update timer
            // Start the update timer with a simple reentrancy guard
            bool updating = false;
            _updateTimer = new Timer((state) =>
            {
                if (updating) return;
                var loop = Application.MainLoop;
                if (loop == null) return;
                try
                {
                    updating = true;
                    loop.Invoke(() =>
                    {
                        UpdateMessagesView(chatService);
                        UpdateLogsView(chatService);
                        UpdatePeersView(chatService);
                        UpdatePeerInfo(chatService);
                    });
                }
                finally
                {
                    updating = false;
                }
            }, null, 0, 300);

            Application.Run();
        }
        catch (Exception ex)
        {
            try
            {
                MessageBox.ErrorQuery("GUI Error", ex.Message, "OK");
            }
            catch
            {
            }
            throw;
        }
        finally
        {
            _updateTimer?.Dispose();
            chatService.Stop();
        }
    }

    private static void OnHelp()
    {
        MessageBox.Query("Help",
            "F1: Help\n" +
            "F2: Switch to Messages tab\n" +
            "F3: Switch to Logs tab\n" +
            "F4: Switch to Peers tab\n" +
            "Esc: Quit\n" +
            "Ctrl+Q: Quit\n\n" +
            "Use arrow keys to scroll in logs and messages.\n" +
            "Type messages and press Enter to send.",
            "OK");
    }

    private static void SwitchToTab(int tabIndex)
    {
        if (_tabView == null) return;

        TabView.Tab? targetTab = tabIndex switch
        {
            0 => _messagesTab,
            1 => _logsTab,
            2 => _peersTab,
            _ => null
        };

        if (targetTab != null)
        {
            _tabView.SelectedTab = targetTab;
        }
    }

    private static void OnMessagesKeyPress(View.KeyEventEventArgs args)
    {
        if (args.KeyEvent.Key == Key.CursorUp || args.KeyEvent.Key == Key.CursorDown ||
            args.KeyEvent.Key == Key.PageUp || args.KeyEvent.Key == Key.PageDown)
        {
            _userHasScrolledMessages = true;

            // Check if user has scrolled to the bottom - if so, re-enable auto-scrolling
            if (_messagesTextView != null && IsAtBottom(_messagesTextView))
            {
                _autoScrollMessages = true;
            }
            else
            {
                _autoScrollMessages = false;
            }
        }
    }

    private static void OnLogsKeyPress(View.KeyEventEventArgs args)
    {
        if (args.KeyEvent.Key == Key.CursorUp || args.KeyEvent.Key == Key.CursorDown ||
            args.KeyEvent.Key == Key.PageUp || args.KeyEvent.Key == Key.PageDown)
        {
            _userHasScrolledLogs = true;

            // Check if user has scrolled to the bottom - if so, re-enable auto-scrolling
            if (_logsTextView != null && IsAtBottom(_logsTextView))
            {
                _autoScrollLogs = true;
            }
            else
            {
                _autoScrollLogs = false;
            }
        }
    }

    private static void OnInputFieldKeyPress(View.KeyEventEventArgs args)
    {
        var key = args.KeyEvent.Key;
        if (key == Key.Enter)
        {
            _sendButton?.OnClicked();
            args.Handled = true;
        }
        else if (key == Key.Tab)
        {
            _sendButton?.SetFocus();
            args.Handled = true;
        }
        else if (key == Key.Esc)
        {
            if (_inputField != null)
            {
                _inputField.Text = "";
            }
            args.Handled = true;
        }
    }

    private static void OnSendButtonClicked(ChatService chatService, string nickName)
    {
        try
        {
            string message = _inputField?.Text?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (_inputField != null)
            {
                _inputField.Text = "";
                _inputField.SetFocus();
            }

            chatService.Publish(message, nickName);

            _autoScrollMessages = true;
            _userHasScrolledMessages = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending message: {ex.Message}");
        }
    }

    private static void OnExitButtonClicked()
    {
        Application.RequestStop();
    }

    private static void OnGlobalKeyPress(View.KeyEventEventArgs args)
    {
        if (args.KeyEvent.Key == (Key.Q | Key.CtrlMask) || args.KeyEvent.Key == Key.Esc)
        {
            Application.RequestStop();
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == Key.F2)
        {
            SwitchToTab(0);
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == Key.F3)
        {
            SwitchToTab(1);
            args.Handled = true;
        }
        else if (args.KeyEvent.Key == Key.F4)
        {
            SwitchToTab(2);
            args.Handled = true;
        }
    }

    private static void UpdateMessagesView(ChatService chatService)
    {
        if (_messagesTextView == null) return;

        lock (chatService.Messages)
        {
            var currentText = string.Join("\n", chatService.Messages);
            if (_messagesTextView.Text?.ToString() != currentText)
            {
                _messagesTextView.Text = currentText;

                if ((!_userHasScrolledMessages || _autoScrollMessages) && _messagesTextView.Lines > 0)
                {
                    ScrollToEnd(_messagesTextView);
                }
            }
        }
    }

    private static void UpdateLogsView(ChatService chatService)
    {
        if (_logsTextView == null) return;

        lock (chatService.Logs)
        {
            var currentText = string.Join("\n", chatService.Logs);
            if (_logsTextView.Text?.ToString() != currentText)
            {
                _logsTextView.Text = currentText;

                if ((!_userHasScrolledLogs || _autoScrollLogs) && _logsTextView.Lines > 0)
                {
                    ScrollToEnd(_logsTextView);
                }
            }
        }
    }

    private static void UpdatePeersView(ChatService chatService)
    {
        if (_peersTextView == null) return;

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
            peersText += "No peers connected yet.\n";
            peersText += "Waiting for peer connections...";
        }

        if (_peersTextView.Text?.ToString() != peersText)
        {
            _peersTextView.Text = peersText;
        }
    }

    private static void UpdatePeerInfo(ChatService chatService)
    {
        if (_peerIdLabel != null)
        {
            _peerIdLabel.Text = $"Peer ID: {chatService.LocalPeerId}";
        }

        if (_multiAddrLabel != null)
        {
            _multiAddrLabel.Text = "Multiaddr: [Listening on configured addresses]";
        }
    }

    private static bool IsAtBottom(TextView textView)
    {
        if (textView.Lines == 0) return true;

        int visibleLines = Math.Max(0, textView.Frame.Height);
        int totalLines = textView.Lines;
        int topRow = Math.Max(0, textView.TopRow);

        return (topRow + visibleLines) >= totalLines;
    }

    private static void ScrollToEnd(TextView textView)
    {
        if (textView == null || textView.Text == null)
            return;

        string text = textView.Text.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
            return;

        int lineCount = text.Count(c => c == '\n') + 1;
        int targetRow = Math.Max(0, lineCount - 1);
        textView.TopRow = Math.Max(0, targetRow - Math.Max(0, textView.Frame.Height - 1));
        textView.CursorPosition = new Point(0, targetRow);
        textView.SetNeedsDisplay();
    }
}
