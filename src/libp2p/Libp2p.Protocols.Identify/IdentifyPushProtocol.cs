// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/tree/master/identify
/// </summary>
public class IdentifyPushProtocol(IProtocolStackSettings protocolStackSettings, IdentifyProtocolSettings? settings = null, PeerStore? peerStore = null, ILoggerFactory? loggerFactory = null)
    : IdentifyProtocolBase(protocolStackSettings, settings, peerStore, loggerFactory), ISessionProtocol<ulong, ulong>
{
    public string Id => "/ipfs/id/push/1.0.0";

    public async Task<ulong> DialAsync(IChannel channel, ISessionContext context, ulong idVersion)
    {
        _logger?.LogDebug("Pushing identity update");
        await SendIdentity(channel, context, idVersion);
        return idVersion;
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        _logger?.LogDebug("Receiving identity update");
        await ReadAndVerifyIndentity(channel, context);
    }
}
