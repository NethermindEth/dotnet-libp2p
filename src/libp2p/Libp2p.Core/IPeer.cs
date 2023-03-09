// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IPeer
{
    Identity Identity { get; set; }
    MultiAddr Address { get; set; }
}
