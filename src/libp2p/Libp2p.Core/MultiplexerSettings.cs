// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;
public class MultiplexerSettings
{
    private readonly List<IProtocol> _multiplexers = [];

    public IEnumerable<IProtocol> Multiplexers => _multiplexers;

    public void Add(IProtocol multiplexerProtocol)
    {
        _multiplexers.Add(multiplexerProtocol);
    }
}
