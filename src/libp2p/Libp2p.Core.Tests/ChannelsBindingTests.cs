namespace Libp2p.Core.Tests;

public class ChannelsBindingTests
{
    [Test]
    public async Task Test_DownchannelClosesUpChannel_WhenBound()
    {
        Channel downChannel = new();
        Channel upChannel = new();
        Channel downChannelFromProtocolPov = downChannel.Reverse as Channel;
        downChannelFromProtocolPov.Bind(upChannel);

        await downChannel.CloseAsync();
        Assert.That(upChannel.IsClosed, Is.True);
    }
}
