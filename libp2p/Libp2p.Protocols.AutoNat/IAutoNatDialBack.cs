// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.AutoNat.Dto;

namespace Nethermind.Libp2p.Protocols.AutoNat;

/// <summary>Performs the AutoNAT v2 dial back for the server.</summary>
public interface IAutoNatDialBack
{
    /// <summary>Whether a transport for <paramref name="addr"/> is available.</summary>
    bool CanDial(Multiaddress addr);

    /// <summary>
    /// Dials <paramref name="addr"/> on a new connection, never an existing session with <paramref name="peer"/>,
    /// sends <paramref name="nonce"/> on <c>/libp2p/autonat/2/dial-back</c> and closes the connection.
    /// </summary>
    /// <returns><see cref="DialStatus.EDialError"/> if the dial failed, <see cref="DialStatus.EDialBackError"/> if the nonce was not delivered, otherwise <see cref="DialStatus.Ok"/>.</returns>
    Task<DialStatus> DialBackAsync(PeerId peer, Multiaddress addr, ulong nonce, CancellationToken token);
}
