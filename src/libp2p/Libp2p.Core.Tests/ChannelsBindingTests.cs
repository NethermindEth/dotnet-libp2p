// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Tests;

public class ChannelsBindingTests
{
    [Test]
    public async Task Test_DownchannelClosesUpChannel_WhenBound()
    {
        Channel downChannel = new();
        Channel upChannel = new();
        Channel downChannelFromProtocolPov = (Channel)downChannel.Reverse;
        downChannelFromProtocolPov.Bind(upChannel);

        await downChannel.CloseAsync();
        Assert.That(upChannel.IsClosed, Is.True);
    }
}
