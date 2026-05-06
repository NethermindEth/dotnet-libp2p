using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DnsClient;
using DnsClient.Protocol;

namespace Nethermind.Libp2p.Core;

public class DnsClientLookup : IDnsLookup
{
    private readonly LookupClient _lookup;

    public DnsClientLookup() => _lookup = new LookupClient();

    public async Task<IEnumerable<string>> QueryTxtAsync(string name)
    {
        IDnsQueryResponse result = await _lookup.QueryAsync(name, QueryType.TXT);
        return result.Answers.TxtRecords().SelectMany(r => r.Text ?? Enumerable.Empty<string>());
    }

    public async Task<IEnumerable<System.Net.IPAddress>> QueryAAsync(string name)
    {
        IDnsQueryResponse result = await _lookup.QueryAsync(name, QueryType.A);
        return result.Answers.ARecords().Select(r => r.Address);
    }

    public async Task<IEnumerable<System.Net.IPAddress>> QueryAaaaAsync(string name)
    {
        IDnsQueryResponse result = await _lookup.QueryAsync(name, QueryType.AAAA);
        return result.Answers.AaaaRecords().Select(r => r.Address);
    }
}
