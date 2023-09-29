// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Text;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     https://github.com/libp2p/specs/blob/master/addressing/README.md
/// </summary>
public struct Multiaddr
{
    // override object.Equals
    public override bool Equals(object obj) =>
        (obj is Multiaddr dst) && ((dst._segments is null && _segments is null) || (dst._segments is not null && _segments is not null && dst._segments.SequenceEqual(_segments)));

    // override object.GetHashCode
    public override int GetHashCode() => ToString().GetHashCode();

    private struct Segment
    {
        public Segment(Enums.Multiaddr type, string? parameter)
        {
            Type = type;
            Parameter = parameter;
        }

        public Enums.Multiaddr Type { get; init; }
        public string? Parameter { get; init; }
    }

    private Segment[]? _segments = null;

    public Multiaddr()
    {
    }

    public static bool operator ==(Multiaddr lhs, Multiaddr rhs)
    {
        return lhs.Equals(rhs);
    }

    public static bool operator !=(Multiaddr lhs, Multiaddr rhs) => !(lhs == rhs);

    public override string ToString()
    {
        return string.Join("", _segments
            .Select(s =>
                s.Parameter is null
                    ? new object[] { $"/{ToString(s.Type)}" }
                    : new object[] { $"/{ToString(s.Type)}", $"/{s.Parameter}" })
            .SelectMany(s => s));
    }

    public static Multiaddr From(params object[] segments)
    {
        List<Segment> segs = new();
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i] is not Enums.Multiaddr addr)
            {
                throw new ArgumentException($"{segments[i]} is expected to be a multiaddress segment id");
            }

            if (ToProto(addr).isParametrized)
            {
                segs.Add(new Segment(addr, segments[++i].ToString()));
            }
        }

        return new Multiaddr { _segments = segs.ToArray() };
    }

    public static implicit operator Multiaddr(string value)
    {
        return From(value);
    }
    public Multiaddr(string value)
    {
        this = From(value);
    }

    public string? At(Enums.Multiaddr section)
    {
        return _segments?.FirstOrDefault(x => x.Type == section).Parameter;
    }

    public bool Has(Enums.Multiaddr section)
    {
        return _segments?.Any(x => x.Type == section) ?? false;
    }

    public Multiaddr Append(Enums.Multiaddr at, string? value)
    {
        Segment[] newSegments = { new(at, value) };
        return new Multiaddr { _segments = _segments is not null ? _segments.Concat(newSegments).ToArray() : newSegments };
    }

    public Multiaddr Replace(Enums.Multiaddr at, Enums.Multiaddr newAt, string? value = null)
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

        return new Multiaddr { _segments = newSegments };
    }

    public Multiaddr Replace(Enums.Multiaddr at, string? value = null)
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

        return new Multiaddr { _segments = newSegments };
    }

    private static Multiaddr From(string multiAddr)
    {
        string[] vals = multiAddr.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<Segment> segments = new();
        for (int i = 0; i < vals.Length; i++)
        {
            (Enums.Multiaddr type, bool isParametrized) segment = ToProto(vals[i]);
            segments.Add(new Segment { Type = segment.type, Parameter = segment.isParametrized ? vals[++i] : null });
        }

        return new Multiaddr { _segments = segments.ToArray() };
    }

    private static (Enums.Multiaddr type, bool isParametrized) ToProto(string val)
    {
        return val.ToLower() switch
        {
            "ip4" => ToProto(Enums.Multiaddr.Ip4),
            "ip6" => ToProto(Enums.Multiaddr.Ip6),
            "tcp" => ToProto(Enums.Multiaddr.Tcp),
            "udp" => ToProto(Enums.Multiaddr.Udp),
            "p2p" => ToProto(Enums.Multiaddr.P2p),
            "ws" => ToProto(Enums.Multiaddr.Ws),
            "quic" => ToProto(Enums.Multiaddr.Quic),
            "quic-v1" => ToProto(Enums.Multiaddr.QuicV1),
            _ => ToProto(Enums.Multiaddr.Unknown)
        };
    }

    private static (Enums.Multiaddr type, bool isParametrized) ToProto(Enums.Multiaddr val)
    {
        return val switch
        {
            Enums.Multiaddr.Quic => (val, false),
            Enums.Multiaddr.QuicV1 => (val, false),
            Enums.Multiaddr.Ws => (val, false),
            Enums.Multiaddr.Unknown => (val, false),
            _ => (val, true)
        };
    }

    private static string ToString(Enums.Multiaddr type)
    {
        return type switch
        {
            Enums.Multiaddr.QuicV1 => "quic-v1",
            _ => type.ToString().ToLower(),
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
