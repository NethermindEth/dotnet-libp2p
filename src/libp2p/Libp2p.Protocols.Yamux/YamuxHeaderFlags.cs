// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols;

[Flags]
public enum YamuxHeaderFlags : short
{
    Syn = 1,
    Ack = 2,
    Fin = 4,
    Rst = 8
}
