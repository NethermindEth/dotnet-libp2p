// Temporary namespace bridge to expose legacy Kademlia types under the Nethermind.Libp2p.* root
// TODO: Rename namespaces in individual files and remove this bridge.
using Libp2p.Protocols.KadDht.Kademlia;
namespace Nethermind.Libp2p.Protocols.KadDht.Kademlia;

// Intentionally empty; presence plus using above allows legacy namespace symbols to be found when referenced
// via Nethermind.Libp2p.Protocols.KadDht.Kademlia.* until we do a proper rename.
