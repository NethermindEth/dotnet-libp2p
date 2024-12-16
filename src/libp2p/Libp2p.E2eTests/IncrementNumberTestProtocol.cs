using Nethermind.Libp2p.Core;

namespace Libp2p.E2eTests;

public class IncrementNumberTestProtocol : ISessionProtocol<int, int>
{
    public string Id => "1";

    public async Task<int> DialAsync(IChannel downChannel, ISessionContext context, int request)
    {
        await downChannel.WriteVarintAsync(request);
        return await downChannel.ReadVarintAsync();
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        int request = await downChannel.ReadVarintAsync();
        await downChannel.WriteVarintAsync(request + 1);
    }
}
