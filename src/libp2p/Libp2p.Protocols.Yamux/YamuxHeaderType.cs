namespace Libp2p.Protocols;

public enum YamuxHeaderType : byte
{
    Data = 0,
    WindowUpdate = 1,
    Ping = 2,
    GoAway = 3
}
