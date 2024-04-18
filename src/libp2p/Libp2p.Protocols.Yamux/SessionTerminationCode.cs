// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols;

internal enum SessionTerminationCode
{
    Ok = 0x0,
    ProtocolError = 0x1,
    InternalError = 0x2,
}
