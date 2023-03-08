using System.Text;
using Libp2p.Enums;

namespace Libp2p.Core;

/// <summary>
///     https://github.com/libp2p/specs/blob/master/addressing/README.md
/// </summary>
public struct MultiAddr
{
    private struct Segment
    {
        public Segment(Multiaddr type, string? parameter)
        {
            Type = type;
            Parameter = parameter;
        }

        public Multiaddr Type { get; init; }
        public string? Parameter { get; init; }
    }

    private Segment[] _segments = null;

    public MultiAddr()
    {
    }

    public override string ToString()
    {
        return string.Join("", _segments
            .Select(s =>
                s.Parameter is null
                    ? new object[] { $"/{s.Type.ToString().ToLower()}" }
                    : new object[] { $"/{s.Type.ToString().ToLower()}", $"/{s.Parameter}" })
            .SelectMany(s => s));
    }

    public static MultiAddr From(params object[] segments)
    {
        List<Segment> segs = new();
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i] is not Multiaddr addr)
            {
                throw new ArgumentException($"{segments[i]} is expceted to be a multiaddress segment id");
            }

            if (ToProto(addr).isParametrized)
            {
                segs.Add(new Segment(addr, segments[++i].ToString()));
            }
        }

        return new MultiAddr { _segments = segs.ToArray() };
    }

    public static implicit operator MultiAddr(string value)
    {
        return From(value);
    }

    public string? At(Multiaddr section)
    {
        return _segments.FirstOrDefault(x => x.Type == section).Parameter;
    }

    public bool Has(Multiaddr section)
    {
        return _segments.Any(x => x.Type == section);
    }

    public MultiAddr Append(Multiaddr at, string? value)
    {
        return new MultiAddr { _segments = _segments.Concat(new Segment[] { new(at, value) }).ToArray() };
    }

    public MultiAddr Replace(Multiaddr at, Multiaddr newAt, string? value = null)
    {
        Segment[] newSegments = _segments.ToArray();
        for (int i = 0; i < _segments.Length; i++)
        {
            if (newSegments[i].Type == at)
            {
                newSegments[i] = new Segment(newAt, value);
                break;
            }
        }

        return new MultiAddr { _segments = newSegments };
    }

    public MultiAddr Replace(Multiaddr at, string? value = null)
    {
        Segment[] newSegments = _segments.ToArray();
        for (int i = 0; i < _segments.Length; i++)
        {
            if (newSegments[i].Type == at)
            {
                newSegments[i] = new Segment(at, value);
                break;
            }
        }

        return new MultiAddr { _segments = newSegments };
    }

    private static MultiAddr From(string multiAddr)
    {
        string[] vals = multiAddr.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<Segment> segments = new();
        for (int i = 0; i < vals.Length; i++)
        {
            (Multiaddr type, bool isParametrized) segment = ToProto(vals[i]);
            segments.Add(new Segment { Type = segment.type, Parameter = segment.isParametrized ? vals[++i] : null });
        }

        return new MultiAddr { _segments = segments.ToArray() };
    }

    private static (Multiaddr type, bool isParametrized) ToProto(string val)
    {
        return val.ToLower() switch
        {
            "ip4" => ToProto(Multiaddr.Ip4),
            "ip6" => ToProto(Multiaddr.Ip6),
            "tcp" => ToProto(Multiaddr.Tcp),
            "udp" => ToProto(Multiaddr.Udp),
            "p2p" => ToProto(Multiaddr.P2p),
            "ws" => ToProto(Multiaddr.Ws),
            _ => ToProto(Multiaddr.Unknown)
        };
    }

    private static (Multiaddr type, bool isParametrized) ToProto(Multiaddr val)
    {
        return val switch
        {
            Multiaddr.Ws => (val, false),
            Multiaddr.Unknown => (val, false),
            _ => (val, true)
        };
    }

    public byte[] ToByteArray()
    {
        Span<byte> result = stackalloc byte[256];
        int ptr = 0;
        for (int i = 0; i < _segments.Length; i++)
        {
            VarInt.Encode((int)_segments[i].Type, result, ref ptr);
            if (ToProto(_segments[i].Type).isParametrized)
            {
                ptr += Encoding.UTF8.GetBytes(_segments[++i].Parameter, result[ptr..]);
            }
        }

        return result[..ptr].ToArray();
    }
}
