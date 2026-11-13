// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>Who initiated the underlying transport connection, independently of stream roles.</summary>
public enum ConnectionDirection
{
    Unknown = 0,
    Inbound = 1,
    Outbound = 2,
}
