using System.Text;
using Libp2p.Core;

internal class ChatProtocol : SymetricProtocol, IProtocol
{
    private static readonly ConsoleReader Reader = new();
    public string Id => "/chat/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context, bool isListener)
    {
        Console.Write("> ");
        _ = Task.Run(async () =>
        {
            while (!channel.Token.IsCancellationRequested)
            {
                byte[] buf = new byte[256];
                int read = await channel.Reader.ReadAsync(buf, false, channel.Token);
                Console.Write(Encoding.UTF8.GetString(buf[..read]).Replace("\n\n", "\n> "));
            }
        }, channel.Token);
        while (!channel.Token.IsCancellationRequested)
        {
            string line = await Reader.ReadLineAsync(channel.Token);
            Console.Write("> ");
            byte[] buf = Encoding.UTF8.GetBytes(line + "\n\n");
            await channel.Writer.WriteAsync(buf);
        }
    }
}
