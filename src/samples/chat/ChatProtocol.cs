// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;


internal class ChatProtocol : SymmetricSessionProtocol, ISessionProtocol
{
    internal Action<string>? OnServerMessage;
    internal Action<string>? OnClientMessage;

    public string Id => "/chat/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, ISessionContext context, bool isListener)
    {
        OnClientMessage += (msg) => channel.WriteLineAsync(msg);

        for (; ; )
        {
            string read = await channel.ReadLineAsync();
            OnServerMessage?.Invoke(read);
        }
    }
}
