// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT


using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core.Exceptions;

public class Libp2pException : Exception
{
    public Libp2pException(string? message) : base(message) { }
    public Libp2pException() : base() { }
}

/// <summary>
/// Exception instead of IOResult to signal a channel cannot send or receive data anymore
/// </summary>
public class ChannelClosedException() : Libp2pException("Channel closed");

/// <summary>
/// Appears when libp2p is not set up properly in part of protocol tack, IoC, etc.
/// </summary>
/// <param name="message"></param>
public class Libp2pSetupException(string? message = null) : Libp2pException(message)
{
    public static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null)
        {
            Throw(paramName);
        }
    }

    [DoesNotReturn]
    internal static void Throw(string? paramName) =>
            throw new Libp2pSetupException($"{paramName} is not set during libp2p initialization");
}

/// <summary>
/// Appears when there is already active session for the given peer
/// </summary>
public class SessionExistsException(PeerId remotePeerId) : Libp2pException($"Session is already established with {remotePeerId}");


/// <summary>
/// Appears if connection to peer failed or declined
/// </summary>
public class PeerConnectionException(string? message = null) : Libp2pException(message);

public class DLibp2pException : Libp2pException
{

}
