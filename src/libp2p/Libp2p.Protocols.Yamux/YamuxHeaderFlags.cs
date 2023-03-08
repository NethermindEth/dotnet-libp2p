namespace Libp2p.Protocols;

[Flags]
public enum YamuxHeaderFlags : short
{
    Syn = 1,
    Ack = 2,
    Fin = 4,
    Rst = 8
}
