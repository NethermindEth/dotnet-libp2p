// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols;

public enum YamuxHeaderType : byte
{
    Data = 0,
    WindowUpdate = 1,
    Ping = 2,
    GoAway = 3
}
