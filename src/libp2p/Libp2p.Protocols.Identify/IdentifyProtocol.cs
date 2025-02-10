// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/tree/master/identify
/// </summary>
public class IdentifyProtocol : IdentifyProtocolBase, ISessionProtocol
{
    public string Id => "/ipfs/id/1.0.0";

    public IdentifyProtocol(IProtocolStackSettings protocolStackSettings, IdentifyProtocolSettings? settings = null, PeerStore? peerStore = null, ILoggerFactory? loggerFactory = null)
        : base(protocolStackSettings, settings, peerStore, loggerFactory)
    {
    }

    public async Task DialAsync(IChannel channel, ISessionContext context)
    {
        _logger?.LogInformation("Dial");
        await ReadAndVerifyIndentity(channel, context);
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        _logger?.LogInformation("Listen");
        await SendIdentity(channel, context);
    }
}
