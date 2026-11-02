// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

// Existing synchronous assertions use completed test transports. Production receives always await OnRpcAsync.
internal static class RouterTestExtensions
{
    public static void OnRpc(this PubsubRouter router, PeerId peer, Rpc rpc, string? protocolId = null, bool isFirstRpc = true)
    {
        router.OnRpcAsync(peer, rpc, protocolId, isFirstRpc).GetAwaiter().GetResult();
        router.FlushOutboundAsync().GetAwaiter().GetResult();
    }

    public static CancellationToken OutboundConnection(this PubsubRouter router, Multiaddress address, string protocol,
        Task closed, Action<Rpc> send)
    {
        PubsubRouter.PubsubPeer? peer = router.OutboundConnection(address, protocol, closed, (bytes, _) =>
        {
            send(Decode(bytes));
            return ValueTask.FromResult(IOResult.Ok);
        }, () => ValueTask.CompletedTask, () => Task.CompletedTask);
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        return peer?.TokenSource.Token ?? new CancellationToken(true);
    }

    public static (CancellationToken Token, Action SuppressReconnection) InboundConnection(this PubsubRouter router,
        Multiaddress address, string protocol, Task closed, Func<Task> dial) =>
        router.InboundConnection(address, protocol, closed, dial, () => Task.CompletedTask);

    public static Rpc Decode(ReadOnlyMemory<byte> frame)
    {
        int offset = 0;
        while ((frame.Span[offset++] & 0x80) != 0) { }
        return Rpc.Parser.ParseFrom(frame.Span[offset..]);
    }
}
