namespace Libp2p.Protocols.KadDht;

/// <summary>
/// Represents a message in the Kademlia DHT protocol.
/// </summary>
/// <typeparam name="TKey">The type of the key in the DHT.</typeparam>
public class KadDhtMessage<TKey>
{
    /// <summary>
    /// Gets or sets the type of the message.
    /// </summary>
    public KadDhtMessageType MessageType { get; set; }

    /// <summary>
    /// Gets or sets the key associated with the message.
    /// </summary>
    public TKey? Key { get; set; }

    /// <summary>
    /// Gets or sets any additional data associated with the message.
    /// </summary>
    public byte[]? Data { get; set; }
}

/// <summary>
/// Defines the types of messages in the Kademlia DHT protocol.
/// </summary>
public enum KadDhtMessageType
{
    /// <summary>
    /// A ping message to check if a node is alive.
    /// </summary>
    Ping,

    /// <summary>
    /// A response to a ping message.
    /// </summary>
    Pong,

    /// <summary>
    /// A request to find nodes closest to a key.
    /// </summary>
    FindNode,

    /// <summary>
    /// A response containing nodes closest to a requested key.
    /// </summary>
    Neighbours
} 