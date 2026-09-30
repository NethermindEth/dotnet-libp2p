// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.AutoTls;

public class AutoTlsException : Exception
{
    public AutoTlsException(string message) : base(message) { }
    public AutoTlsException(string message, Exception inner) : base(message, inner) { }
}
