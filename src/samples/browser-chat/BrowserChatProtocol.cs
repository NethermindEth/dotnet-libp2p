// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;

namespace BrowserChat;

internal sealed class BrowserChatProtocol : ISessionProtocol<string, string>
{
    public string Id => "/chat/1.0.0";

    public async Task<string> DialAsync(IChannel downChannel, ISessionContext context, string request)
    {
        byte[] requestBytes = Encoding.UTF8.GetBytes(request.TrimEnd('\r', '\n') + "\n\n");
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(requestBytes)).OrThrow();

        ReadOnlySequence<byte> response = await downChannel.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow();
        _ = downChannel.CloseAsync();

        return Encoding.UTF8.GetString(response).Replace("\r", "").Replace("\n\n", "").TrimEnd('\n');
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        for (; ; )
        {
            ReadOnlySequence<byte> request;
            try
            {
                request = await downChannel.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow();
            }
            catch (ChannelClosedException)
            {
                return;
            }

            string message = Encoding.UTF8.GetString(request).Replace("\r", "").Replace("\n\n", "").TrimEnd('\n');
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            byte[] responseBytes = Encoding.UTF8.GetBytes(message.TrimEnd('\r', '\n') + "\n\n");
            await downChannel.WriteAsync(new ReadOnlySequence<byte>(responseBytes)).OrThrow();
        }
    }
}
