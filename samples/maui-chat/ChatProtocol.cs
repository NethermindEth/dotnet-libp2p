// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace MauiChat;

internal class ChatProtocol : SymmetricSessionProtocol, ISessionProtocol
{
    internal Action<string>? OnServerMessage;
    internal Func<string, Task<IOResult>>? OnClientMessage;

    public string Id => "/chat/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, ISessionContext context, bool isListener)
    {
        Func<string, Task<IOResult>> send = msg => channel.WriteLineAsync(msg).AsTask();
        OnClientMessage = send;
        try
        {
            for (; ; )
            {
                string read = await channel.ReadLineAsync();
                OnServerMessage?.Invoke(read);
            }
        }
        finally
        {
            if (ReferenceEquals(OnClientMessage, send))
            {
                OnClientMessage = null;
            }
        }
    }
}
