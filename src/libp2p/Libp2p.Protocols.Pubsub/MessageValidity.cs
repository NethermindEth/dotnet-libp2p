// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub;
public enum MessageValidity
{
    Accepted,
    Ignored,
    Rejected,
    Throttled
}
