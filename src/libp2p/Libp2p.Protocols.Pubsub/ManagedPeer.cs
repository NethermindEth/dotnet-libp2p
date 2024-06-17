// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;
internal class ManagedPeer(ILocalPeer peer)
{
    internal async Task<IRemotePeer> DialAsync(Multiaddress[] addrs, CancellationToken token)
    {
        Dictionary<Multiaddress, CancellationTokenSource> cancellations = new();
        foreach (Multiaddress addr in addrs)
        {
            cancellations[addr] = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        Task timoutTask = Task.Delay(15_000, token);
        Task<Task<IRemotePeer>> firstConnectedTask = Task.WhenAny(addrs
            .Select(addr => peer.DialAsync(addr, cancellations[addr].Token)));

        Task wait = await Task.WhenAny(firstConnectedTask, timoutTask);

        if (wait == timoutTask)
        {
            throw new TimeoutException();
        }

        IRemotePeer firstConnected = firstConnectedTask.Result.Result;

        foreach (KeyValuePair<Multiaddress, CancellationTokenSource> c in cancellations)
        {
            if (c.Key != firstConnected.Address)
            {
                c.Value.Cancel(false);
            }
        }

        return firstConnected;
    }
}
