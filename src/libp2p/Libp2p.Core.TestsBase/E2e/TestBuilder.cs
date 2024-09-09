// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

public class TestBuilder(ChannelBus? commmonBus = null, IServiceProvider? serviceProvider = null) : PeerFactoryBuilderBase<TestBuilder, PeerFactory>(serviceProvider)
{
    protected override ProtocolStack BuildStack()
    {
        return Over(new TestMuxerProtocol(commmonBus ?? new ChannelBus(), new TestContextLoggerFactory()))
            .AddAppLayerProtocol<TestPingProtocol>();
    }
}
