namespace Nethermind.Channels;

public static class UnwrapResultExtensions
{
    public static async ValueTask OrThrow(this ValueTask<IOResult> self)
    {
        if (self.IsCompleted && self.Result != IOResult.Ok)
        {
            throw new ChannelClosedException();
        }

        IOResult result = await self.AsTask();

        if (result != IOResult.Ok)
        {
            throw new ChannelClosedException();
        }
    }
    
    public static async ValueTask<ReadResult> OrThrow(this ValueTask<ReadResult> self)
    {
        if (self.IsCompleted && self.Result.Result != IOResult.Ok)
        {
            throw new ChannelClosedException();
        }

        ReadResult result = await self.AsTask();

        if (result.Result != IOResult.Ok)
        {
            throw new ChannelClosedException();
        }
        else
        {
            return result;
        }
    }
}
