// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public static class MultiaddrProtocolRegistry
{
    public static void EnsureRegistered() => MultiaddressProtocolRegistration.EnsureRegistered();
}