// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

public class TestBuilder(IServiceProvider? serviceProvider = null) : PeerFactoryBuilderBase<TestBuilder, PeerFactory>(serviceProvider)
{
    protected override ProtocolRef[] BuildStack(ProtocolRef[] additionalProtocols)
    {
        var root = Get<TestMuxerProtocol>();

        Connect([root],
            [
                Get<TestPingProtocol>(),
                .. additionalProtocols
            ]);

        return [root];
    }
}
