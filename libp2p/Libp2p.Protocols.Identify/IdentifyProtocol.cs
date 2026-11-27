// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/tree/master/identify
/// </summary>
public class IdentifyProtocol : IdentifyProtocolBase, ISessionProtocol<IdentifyResult>
{
    public const string ProtocolId = "/ipfs/id/1.0.0";
    public string Id => ProtocolId;

    public IdentifyProtocol(IProtocolStackSettings protocolStackSettings, IdentifyProtocolSettings? settings = null, IPeerStore? peerStore = null, ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
        : base(protocolStackSettings, settings, peerStore, loggerFactory, timeProvider)
    {
    }

    public async Task<IdentifyResult> DialAsync(IChannel channel, ISessionContext context)
    {
        _logger?.LogInformation("Dial");
        IdentifyResult result = await ReadAndVerifyIdentity(channel, context);
        State state = context.State;
        if (!state.IsRemoteIdentityAuthenticated || result.ObservedAddress is not { Length: > 0 } observation) return result;

        CancellationToken connectionToken = context.Token;
        CancellationToken invocationToken = context.UpgradeOptions?.CancellationToken ?? default;
        if (connectionToken.IsCancellationRequested || invocationToken.IsCancellationRequested || result.PeerId == context.Peer.Identity.PeerId) return result;

        PublicAddressDiscovery? discovery;
        try { discovery = context.Peer.GetPublicAddressDiscovery(); }
        catch (ObjectDisposedException) { return result; } // The peer can close during Identify.
        discovery?.Record(result.PeerId, state.RemoteAddress, observation.Span, connectionToken, invocationToken);
        return result;
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        _logger?.LogInformation("Listen");
        await SendIdentity(channel, context);
    }
}
