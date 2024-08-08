// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IId
{
    /// <summary>
    ///     Id used to during connection establishedment, exchanging information about protocol versions and so on
    /// </summary>
    string Id { get; }
}

public interface ITransportProtocol : IId
{
    /// <summary>
    ///     Actively dials a peer
    /// </summary>
    /// <param name="downChannel">A channel to communicate with a bottom layer protocol</param>
    /// <param name="upChannelFactory">Factory that spawns new channels used to interact with top layer protocols</param>
    /// <param name="context">Holds information about local and remote peers</param>
    /// <returns></returns>
    Task DialAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context);

    /// <summary>
    ///     Opens a channel to listen to a remote peer
    /// </summary>
    /// <param name="downChannel">A channel to communicate with a bottom layer protocol</param>
    /// <param name="upChannelFactory">Factory that spawns new channels used to interact with top layer protocols</param>
    /// <param name="context">Holds information about local and remote peers</param>
    /// <returns></returns>
    Task ListenAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context);
}

public interface IProtocol : IId
{
    /// <summary>
    ///     Actively dials a peer
    /// </summary>
    /// <param name="downChannel">A channel to communicate with a bottom layer protocol</param>
    /// <param name="upChannelFactory">Factory that spawns new channels used to interact with top layer protocols</param>
    /// <param name="context">Holds information about local and remote peers</param>
    /// <returns></returns>
    Task DialAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context);

    /// <summary>
    ///     Opens a channel to listen to a remote peer
    /// </summary>
    /// <param name="downChannel">A channel to communicate with a bottom layer protocol</param>
    /// <param name="upChannelFactory">Factory that spawns new channels used to interact with top layer protocols</param>
    /// <param name="context">Holds information about local and remote peers</param>
    /// <returns></returns>
    Task ListenAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context);
}
