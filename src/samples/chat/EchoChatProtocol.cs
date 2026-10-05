// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;

internal sealed class EchoChatProtocol : ISessionProtocol<string, string>
{
    public string Id => "/chat/1.0.0";

    public async Task<string> DialAsync(IChannel channel, ISessionContext context, string request)
    {
        await WriteMessageAsync(channel, request);
        ReadOnlySequence<byte> response = await channel.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow();
        _ = channel.CloseAsync();

        return DecodeMessage(response);
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

            string message = DecodeMessage(request);
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            await WriteMessageAsync(downChannel, message);
        }
    }

    private static async Task WriteMessageAsync(IChannel channel, string message)
    {
        byte[] buf = Encoding.UTF8.GetBytes(message.TrimEnd('\r', '\n') + "\n\n");
        await channel.WriteAsync(new ReadOnlySequence<byte>(buf));
    }

    private static string DecodeMessage(ReadOnlySequence<byte> data) =>
        Encoding.UTF8.GetString(data).Replace("\r", "").Replace("\n\n", "").TrimEnd('\n');
}
