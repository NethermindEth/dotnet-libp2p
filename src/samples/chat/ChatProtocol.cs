// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text;
using Nethermind.Libp2p.Core;
using Microsoft.Extensions.Logging;

internal class ChatProtocol : SymmetricSessionProtocol, ISessionProtocol
{
    private readonly ILogger? _logger;
    private TerminalUI? _ui;

    public ChatProtocol(ILogger? logger = null)
    {
        _logger = logger;
    }

    public string Id => "/chat/1.0.0";

    public void SetUI(TerminalUI ui)
    {
        _ui = ui;
    }

    protected override async Task ConnectAsync(IChannel channel, ISessionContext context, bool isListener)
    {
        _logger?.LogInformation("Chat protocol connected, is listener: {isListener}", isListener);

        _ = Task.Run(async () =>
        {
            try
            {
                for (; ; )
                {
                    ReadOnlySequence<byte> read = await channel.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow();
                    string message = Encoding.UTF8.GetString(read).Replace("\r", "").Replace("\n\n", "");

                    if (_ui != null)
                    {
                        var chatMessage = new ChatMessage
                        {
                            Content = message,
                            SenderId = "Remote Peer",
                            Timestamp = DateTime.Now
                        };
                        _ui.AddChatMessage(chatMessage);
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("{0}", message);
                        Console.ForegroundColor = ConsoleColor.Gray;
                        Console.Write("> ");
                    }

                    _logger?.LogDebug("Received message: {message}", message);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error reading from chat channel");
                _ui?.AddLogMessage($"Error reading from chat channel: {ex.Message}");
            }
        });

        if (_ui != null)
        {
            _ui.MessageSent += async (sender, message) =>
            {
                try
                {
                    byte[] buf = Encoding.UTF8.GetBytes(message + "\n\n");
                    await channel.WriteAsync(new ReadOnlySequence<byte>(buf));
                    _logger?.LogDebug("Sent message: {message}", message);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error sending message");
                    _ui.AddLogMessage($"Error sending message: {ex.Message}");
                }
            };

            _ui.ExitRequested += async (sender, e) =>
            {
                _logger?.LogInformation("Exit requested by user");
                try
                {
                    await channel.CloseAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error closing channel");
                }
            };
        }
        else
        {
            var reader = new ConsoleReader();
            for (; ; )
            {
                string line = await reader.ReadLineAsync();
                if (line == "exit")
                {
                    return;
                }
                Console.Write("> ");
                byte[] buf = Encoding.UTF8.GetBytes(line + "\n\n");
                await channel.WriteAsync(new ReadOnlySequence<byte>(buf));
            }
        }

        await new TaskCompletionSource().Task;
    }
}
