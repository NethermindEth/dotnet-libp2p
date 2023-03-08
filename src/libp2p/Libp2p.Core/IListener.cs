using System.Runtime.CompilerServices;

namespace Libp2p.Core;

public interface IListener
{
    MultiAddr Address { get; }
    event OnConnection OnConnection;
    Task DisconectAsync();
    TaskAwaiter GetAwaiter();
}
