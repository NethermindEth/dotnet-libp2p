// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.NatTraversal;

public sealed class NatHolePunchProtocol : ISessionProtocol<HolePunchRequest, HolePunchResult>
{
    private static readonly HolePunchRequest EmptyRequest = new([]);
    private readonly ILogger<NatHolePunchProtocol>? _logger;

    public NatHolePunchProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<NatHolePunchProtocol>();
    }

    public string Id => "/libp2p/dcutr";

    public event EventHandler<HolePunchRequestedEventArgs>? HolePunchRequested;

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        HolePunchMessage connect = await ReadMessageAsync(downChannel).ConfigureAwait(false);
        if (connect.Type != HolePunchMessageType.Connect)
            throw new InvalidOperationException($"Expected CONNECT, received {connect.Type}.");

        PeerId? remotePeerId = context.State.RemotePeerId;
        HolePunchRequested?.Invoke(this, new HolePunchRequestedEventArgs(remotePeerId, connect.ObservedAddresses));

        Multiaddress[] observedAddresses = [.. context.Peer.ListenAddresses];
        _logger?.LogDebug("Received DCUtR CONNECT from {PeerId} with {AddressCount} observed address(es).",
            remotePeerId, connect.ObservedAddresses.Count);

        await WriteMessageAsync(downChannel, HolePunchMessage.Connect(observedAddresses)).ConfigureAwait(false);

        HolePunchMessage sync = await ReadMessageAsync(downChannel).ConfigureAwait(false);
        if (sync.Type != HolePunchMessageType.Sync)
            throw new InvalidOperationException($"Expected SYNC, received {sync.Type}.");

        await downChannel.CloseAsync().ConfigureAwait(false);
    }

    public async Task<HolePunchResult> DialAsync(IChannel downChannel, ISessionContext context, HolePunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        await WriteMessageAsync(downChannel, HolePunchMessage.Connect(request.ObservedAddresses)).ConfigureAwait(false);
        HolePunchMessage response = await ReadMessageAsync(downChannel).ConfigureAwait(false);

        if (response.Type != HolePunchMessageType.Connect)
            throw new InvalidOperationException($"Expected CONNECT, received {response.Type}.");

        await WriteMessageAsync(downChannel, HolePunchMessage.Sync()).ConfigureAwait(false);
        await downChannel.CloseAsync().ConfigureAwait(false);

        _logger?.LogDebug("Completed DCUtR coordination with {AddressCount} remote observed address(es).",
            response.ObservedAddresses.Count);

        return new HolePunchResult(response.ObservedAddresses);
    }

    public Task<HolePunchResult> DialAsync(IChannel downChannel, ISessionContext context)
        => DialAsync(downChannel, context, EmptyRequest);

    private static async Task<HolePunchMessage> ReadMessageAsync(IChannel channel)
    {
        int length = await channel.ReadVarintAsync().ConfigureAwait(false);
        ReadOnlySequence<byte> payload = await channel.ReadAsync(length).OrThrow().ConfigureAwait(false);
        return HolePunchMessageCodec.Decode(payload.ToArray());
    }

    private static async Task WriteMessageAsync(IChannel channel, HolePunchMessage message)
    {
        await channel.WriteSizeAndDataAsync(HolePunchMessageCodec.Encode(message)).ConfigureAwait(false);
    }
}

public sealed record HolePunchRequest(IReadOnlyCollection<Multiaddress> ObservedAddresses);

public sealed record HolePunchResult(IReadOnlyList<Multiaddress> RemoteObservedAddresses);

public sealed class HolePunchRequestedEventArgs : EventArgs
{
    public HolePunchRequestedEventArgs(PeerId? remotePeerId, IReadOnlyList<Multiaddress> observedAddresses)
    {
        RemotePeerId = remotePeerId;
        ObservedAddresses = observedAddresses;
    }

    public PeerId? RemotePeerId { get; }
    public IReadOnlyList<Multiaddress> ObservedAddresses { get; }
}
