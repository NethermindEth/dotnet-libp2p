using Libp2p.Core;

namespace Libp2p.Protocols;

/// <summary>
///     https://github.com/multiformats/multistream-select
/// </summary>
public class MultistreamProtocol : IProtocol
{
    private const string ProtocolNotSupported = "na";
    public string Id => "/multistream/1.0.0";

    public async Task DialAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context)
    {
        if (!await SendHello(channel))
        {
            await channel.CloseAsync();
            return;
        }

        IProtocol? selected = null;
        foreach (IProtocol selector in channelFactory.SubProtocols)
        {
            await channel.Writer.WriteLineAsync(selector.Id);
            string selectorLine = await channel.Reader.ReadLineAsync();
            if (selectorLine == selector.Id)
            {
                selected = selector;
                break;
            }

            if (selectorLine != ProtocolNotSupported)
            {
                break;
            }
        }

        if (selected is null)
        {
            await channel.CloseAsync();
            return;
        }

        await channelFactory.SubDialAndBind(channel, context, selected);
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context)
    {
        if (!await SendHello(channel))
        {
            await channel.CloseAsync();
            return;
        }

        IProtocol? selected = null;
        while (!channel.IsClosed)
        {
            string proto = await channel.Reader.ReadLineAsync();
            selected = channelFactory.SubProtocols.FirstOrDefault(x => x.Id == proto);
            if (selected is not null)
            {
                await channel.Writer.WriteLineAsync(selected.Id);
                break;
            }

            await channel.Writer.WriteLineAsync(ProtocolNotSupported);
        }

        if (selected is null)
        {
            await channel.CloseAsync();
            return;
        }

        _ = channelFactory.SubListenAndBind(channel, context, selected);
    }

    private async Task<bool> SendHello(IChannel channel)
    {
        await channel.Writer.WriteLineAsync(Id);
        string line = await channel.Reader.ReadLineAsync();
        return line == Id;
    }
}
