namespace Libp2p.Core;

public interface IPeer
{
    Identity Identity { get; set; }
    MultiAddr Address { get; set; }
}
