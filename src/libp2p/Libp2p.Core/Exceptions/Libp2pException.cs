// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Exceptions;

public class Libp2pException : Exception
{
    public Libp2pException(string? message) : base(message)
    {

    }
    public Libp2pException() : base()
    {

    }
}

public class ChannelClosedException : Libp2pException
{
    public ChannelClosedException()
    {

    }
}

public class Libp2pSetupException(string? message = null) : Libp2pException(message)
{

}
