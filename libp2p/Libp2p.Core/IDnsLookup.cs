using System.Net;

namespace Nethermind.Libp2p.Core;

public interface IDnsLookup
{
    Task<IEnumerable<string>> QueryTxtAsync(string name, CancellationToken token = default);
    Task<IEnumerable<IPAddress>> QueryAAsync(string name, CancellationToken token = default);
    Task<IEnumerable<IPAddress>> QueryAaaaAsync(string name, CancellationToken token = default);
}
