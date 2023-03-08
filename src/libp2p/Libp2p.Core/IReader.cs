using System.Text;

namespace Libp2p.Core;

public interface IReader
{
    async Task<string> ReadLineAsync(bool prependedWithSize = true)
    {
        ulong size = await ReadVarintAsync();
        byte[] buf = new byte[size];
        await ReadAsync(buf);
        return Encoding.UTF8.GetString(buf).TrimEnd('\n');
    }

    Task<ulong> ReadVarintAsync()
    {
        return VarInt.Decode(this);
    }

    Task<int> ReadAsync(byte[] bytes, bool blocking = true, CancellationToken token = default);
}
