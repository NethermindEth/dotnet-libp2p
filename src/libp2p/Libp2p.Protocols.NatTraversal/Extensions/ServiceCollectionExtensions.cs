// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.NatTraversal.Extensions;

public static class ServiceCollectionExtensions
{
    public static IPeerFactoryBuilder AddNatHolePunch(this IPeerFactoryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddProtocol<NatHolePunchProtocol>();
        return builder;
    }
}
