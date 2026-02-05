using System.Net;
using System.Collections.Generic;
using System.Threading.Tasks;
using DnsClient;

namespace Nethermind.Libp2p.Core;

public interface IDnsLookup
{
    Task<IEnumerable<string>> QueryTxtAsync(string name);
    Task<IEnumerable<IPAddress>> QueryAAsync(string name);
    Task<IEnumerable<IPAddress>> QueryAaaaAsync(string name);
}
