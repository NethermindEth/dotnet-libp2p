using System.Net;

namespace Nethermind.Libp2p.Core;

public interface IDnsLookup
{
    Task<IEnumerable<string>> QueryTxtAsync(string name);
    Task<IEnumerable<IPAddress>> QueryAAsync(string name);
    Task<IEnumerable<IPAddress>> QueryAaaaAsync(string name);

    /// <summary>Cancels waiting for the query; override to also cancel the underlying DNS operation.</summary>
    Task<IEnumerable<string>> QueryTxtAsync(string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return QueryTxtAsync(name).WaitAsync(token);
    }

    /// <summary>Cancels waiting for the query; override to also cancel the underlying DNS operation.</summary>
    Task<IEnumerable<IPAddress>> QueryAAsync(string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return QueryAAsync(name).WaitAsync(token);
    }

    /// <summary>Cancels waiting for the query; override to also cancel the underlying DNS operation.</summary>
    Task<IEnumerable<IPAddress>> QueryAaaaAsync(string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return QueryAaaaAsync(name).WaitAsync(token);
    }
}
