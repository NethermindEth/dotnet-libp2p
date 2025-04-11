// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Terminal.Gui;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;


public class ChatMessage
{
    public string Content { get; set; } = string.Empty;

    public string SenderId { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public override string ToString()
    {
        return $"[{Timestamp:HH:mm:ss}] {(string.IsNullOrEmpty(SenderId) ? "You" : SenderId)}: {Content}";
    }
}


public class Peer
{
    public string Id { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public DateTime LastSeen { get; set; } = DateTime.Now;

    public override string ToString()
    {
        return $"{Id} ({Address})";
    }
}

public class TerminalUI
{
    private readonly object _lock = new();
    private readonly List<ChatMessage> _messages = new();
    private readonly List<string> _logs = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private Window _mainWindow;
    private TabView _tabView;
    private TextView _chatView;
    private ListView _peersView;
    private TextView _logsView;
    private TextField _inputField;
    private Button _sendButton;
    private Label _statusLabel;
    private string _peerId = string.Empty;
    private string _address = string.Empty;

    public event EventHandler<string> MessageSent;

    public event EventHandler ExitRequested;

    public void Initialize()
    {
        Application.Init();
        Colors.Base.Normal = Application.Driver.MakeAttribute(Color.White, Color.Blue);

        _mainWindow = new Window("libp2p Chat (Ctrl+X or ESC to exit)")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        _statusLabel = new Label("Peer ID: Not connected | Address: Not connected")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
        };

        _tabView = new TabView()
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(3)
        };

        _chatView = new TextView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true
        };

        // Peers view
        _peersView = new ListView(new List<string>())
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false
        };

        _logsView = new TextView()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true
        };

        _tabView.AddTab(new TabView.Tab("Chat", _chatView), true);
        _tabView.AddTab(new TabView.Tab("Peers", _peersView), false);
        _tabView.AddTab(new TabView.Tab("Logs", _logsView), false);

        _inputField = new TextField("")
        {
            X = 0,
            Y = Pos.Bottom(_tabView),
            Width = Dim.Fill(10),
            Height = 1
        };

        _sendButton = new Button("Send")
        {
            X = Pos.Right(_inputField),
            Y = Pos.Bottom(_tabView),
            Width = 10,
            Height = 1
        };

        var exitButton = new Button("Exit (^X)")
        {
            X = Pos.Right(_sendButton),
            Y = Pos.Bottom(_tabView),
            Width = 10
        };

        _sendButton.Clicked += OnSendButtonClicked;
        exitButton.Clicked += OnExitButtonClicked;
        _inputField.KeyPress += OnInputKeyPress;

        _mainWindow.Add(_statusLabel, _tabView, _inputField, _sendButton, exitButton);

        Application.Top.Add(_mainWindow);

        AddChatMessage(new ChatMessage
        {
            Content = "Welcome to libp2p Chat!",
            SenderId = "System"
        });
    }

    public void Run()
    {
        Application.Run();
    }

    public void UpdateStatus(string peerId, string address)
    {
        lock (_lock)
        {
            _peerId = peerId;
            _address = address;

            Application.MainLoop.Invoke(() =>
            {
                _statusLabel.Text = $"Peer ID: {peerId} | Address: {address}";
            });

            AddLogMessage($"Connected as {peerId}");
            AddLogMessage($"Listening on {address}");
        }
    }

    public void AddChatMessage(ChatMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);

            Application.MainLoop.Invoke(() =>
            {
                var sb = new StringBuilder();
                foreach (var msg in _messages)
                {
                    sb.AppendLine(msg.ToString());
                }
                _chatView.Text = sb.ToString();
                ScrollToEnd(_chatView);
            });
        }
    }

    public void AddLogMessage(string message)
    {
        lock (_lock)
        {
            string formattedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logs.Add(formattedMessage);

            Application.MainLoop.Invoke(() =>
            {
                var sb = new StringBuilder();
                foreach (var log in _logs)
                {
                    sb.AppendLine(log);
                }
                _logsView.Text = sb.ToString();
                ScrollToEnd(_logsView);
            });
        }
    }

    public void AddPeer(string peerId, string address)
    {
        lock (_lock)
        {
            var peer = new Peer
            {
                Id = peerId,
                Address = address,
                LastSeen = DateTime.Now
            };

            _peers[peerId] = peer;

            Application.MainLoop.Invoke(() =>
            {
                _peersView.SetSource(_peers.Values.Select(p => p.ToString()).ToList());
            });

            AddLogMessage($"Peer connected: {peerId}");
        }
    }

    private void OnSendButtonClicked()
    {
        SendMessage();
    }

    private void OnExitButtonClicked()
    {
        try
        {
            AddLogMessage("Closing application...");
            ExitRequested?.Invoke(this, EventArgs.Empty);
            Application.MainLoop.Invoke(() =>
            {
                try
                {
                    Application.RequestStop();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during UI stop: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during exit: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private void OnInputKeyPress(View.KeyEventEventArgs e)
    {
        if (e.KeyEvent.Key == Key.Enter)
        {
            SendMessage();
            e.Handled = true;
        }
        else if (e.KeyEvent.Key == (Key.X | Key.CtrlMask) || e.KeyEvent.Key == Key.Esc)
        {
            OnExitButtonClicked();
            e.Handled = true;
        }
    }

    private void SendMessage()
    {
        var message = _inputField.Text.ToString();
        if (string.IsNullOrWhiteSpace(message))
            return;

        _inputField.Text = NStack.ustring.Empty;

        AddChatMessage(new ChatMessage
        {
            Content = message,
            SenderId = "",
            Timestamp = DateTime.Now
        });

        MessageSent?.Invoke(this, message);
    }

    private void ScrollToEnd(TextView textView)
    {
        if (textView.Text.Length > 0)
        {
            var lines = textView.Text.ToString().Split('\n').Length;
            textView.CursorPosition = new Point(0, Math.Max(0, lines - 1));
        }
    }


    public void Shutdown()
    {
        try
        {
            AddLogMessage("Shutting down...");

            lock (_lock)
            {
                _messages.Clear();
                _logs.Clear();
                _peers.Clear();
            }

            if (Application.MainLoop != null)
            {
                var resetEvent = new ManualResetEventSlim();

                Application.MainLoop.Invoke(() =>
                {
                    try
                    {
                        if (_chatView != null) _chatView.Text = string.Empty;
                        if (_logsView != null) _logsView.Text = string.Empty;
                        if (_peersView != null) _peersView.SetSource(new List<string>());

                        if (_mainWindow != null) _mainWindow.RemoveAll();
                        if (Application.Top != null) Application.Top.RemoveAll();

                        Application.RequestStop();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error during UI cleanup: {ex.Message}");
                    }
                    finally
                    {
                        resetEvent.Set();
                    }
                });

                resetEvent.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during shutdown: {ex.Message}");
        }
    }

    public class TUILogger : ILogger
    {
        private readonly TerminalUI _ui;
        private readonly string _category;

        public TUILogger(TerminalUI ui, string category)
        {
            _ui = ui;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) => new DummyDisposable();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            string prefix = logLevel switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT",
                _ => "LOG"
            };

            string message = formatter(state, exception);
            _ui.AddLogMessage($"[{prefix}] {_category}: {message}");

            if (exception != null)
            {
                _ui.AddLogMessage($"[{prefix}] Exception: {exception.Message}");
                _ui.AddLogMessage($"[{prefix}] StackTrace: {exception.StackTrace}");
            }
        }

        private class DummyDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    public class TUILoggerFactory : ILoggerFactory
    {
        private readonly TerminalUI _ui;

        public TUILoggerFactory(TerminalUI ui)
        {
            _ui = ui;
        }

        public void Dispose() { }

        public ILogger CreateLogger(string categoryName)
        {
            return new TUILogger(_ui, categoryName);
        }

        public void AddProvider(ILoggerProvider provider)
        {
            // Not implemented
        }
    }
}
