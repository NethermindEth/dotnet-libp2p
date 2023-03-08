using System.Runtime.CompilerServices;

namespace Libp2p.Core.TestsBase;

public class TestChannel : IChannel
{
    private readonly Channel _channel;

    public TestChannel()
    {
        _channel = new Channel();
    }

    public IReader Reader => _channel.Reader;
    public IWriter Writer => _channel.Writer;
    public bool IsClosed => _channel.IsClosed;
    public CancellationToken Token => _channel.Token;

    public Task CloseAsync(bool graceful = true)
    {
        return _channel.CloseAsync();
    }

    public void OnClose(Func<Task> action)
    {
        _channel.OnClose(action);
    }

    public TaskAwaiter GetAwaiter()
    {
        return _channel.GetAwaiter();
    }

    public IChannel Reverse()
    {
        return _channel.Reverse;
    }
}
