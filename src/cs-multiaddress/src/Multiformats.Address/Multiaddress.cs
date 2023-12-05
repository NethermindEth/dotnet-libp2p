using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using BinaryEncoding;
using Multiformats.Address.Protocols;
using Multiformats.Hash;
using Org.BouncyCastle.Bcpg;

namespace Multiformats.Address
{
    public class Multiaddress : IEquatable<Multiaddress>
    {
        static Multiaddress()
        {
            Setup<IP4>("ip4", 4, 32, false, ip => {
                if (ip != null)
                {
                        if (ip is IPAddress)
                            return new IP4((IPAddress)ip);
                        else if (ip is string)
                            return new IP4((string)ip);
                        else
                            throw new Exception($"Invalid IP4 address {ip}");
                }

                return new IP4();
            });
            Setup<IP6>("ip6", 41, 128, false, ip => ip != null ? new IP6((IPAddress)ip) : new IP6());
            Setup<TCP>("tcp", 6, 16, false, port => port != null ? new TCP((int)port) : new TCP());
            Setup<UDP>("udp", 17, 16, false, port => port != null ? new UDP((int)port) : new UDP());
            Setup<P2P>("p2p", 420, -1, false, address => address != null ? address is Multihash ? new P2P((Multihash)address) : new P2P((string)address) : new P2P());
            Setup<IPFS>("ipfs", 421, -1, false, address => address != null ? address is Multihash ? new IPFS((Multihash)address) : new IPFS((string)address) : new IPFS());
            Setup<WebSocket>("ws", 477, 0, false, _ => new WebSocket());
            Setup<WebSocketSecure>("wss", 478, 0, false, _ => new WebSocketSecure());
            Setup<DCCP>("dccp", 33, 16, false, port => port != null ? new DCCP((short)port) : new DCCP());
            Setup<SCTP>("sctp", 132, 16, false, port => port != null ? new SCTP((short)port) : new SCTP());
            Setup<Unix>("unix", 400, -1, true, address => address != null ? new Unix((string)address) : new Unix());
            Setup<Onion>("onion", 444, 96, false, address => address != null ? new Onion((string)address) : new Onion());
            Setup<QUIC>("quic", 460, 0, false, _ => new QUIC());
            Setup<QUICv1>("quic-v1", 461, 0, false, _ => new QUICv1());
            Setup<HTTP>("http", 480, 0, false, _ => new HTTP());
            Setup<HTTPS>("https", 443, 0, false, _ => new HTTPS());
            Setup<UTP>("utp", 301, 0, false, _ => new UTP());
            Setup<UDT>("udt", 302, 0, false, _ => new UDT());
            Setup<DNS>("dns", 53, -1, false, address => address != null ? new DNS((string)address) : new DNS());
            Setup<DNS4>("dns4", 54, -1, false, address => address != null ? new DNS4((string)address) : new DNS4());
            Setup<DNS6>("dns6", 55, -1, false, address => address != null ? new DNS6((string)address) : new DNS6());
            Setup<P2PCircuit>("p2p-circuit", 290, 0, false, _ => new P2PCircuit());
            Setup<P2PWebRTCStar>("p2p-webrtc-star", 275, 0, false, _ => new P2PWebRTCStar());
            Setup<P2PWebRTCDirect>("p2p-webrtc-direct", 276, 0, false, _ => new P2PWebRTCStar());
            Setup<P2PWebSocketStar>("p2p-websocket-star", 479, 0, false, _ => new P2PWebSocketStar());
        }

        private class Protocol
        {
            public string Name { get; }
            public int Code { get; }
            public int Size { get; }
            public Func<object, MultiaddressProtocol> Factory { get; }
            public Type Type { get; }
            public bool Path { get; }

            public Protocol(string name, int code, int size, Type type, bool path, Func<object, MultiaddressProtocol> factory)
            {
                Name = name;
                Code = code;
                Size = size;
                Type = type;
                Path = path;
                Factory = factory;
            }
            
        }
        private static readonly List<Protocol> _protocols = new List<Protocol>();

        private static void Setup<TProtocol>(string name, int code, int size, bool path, Func<object, MultiaddressProtocol> factory)
            where TProtocol : MultiaddressProtocol
        {
            _protocols.Add(new Protocol(name, code, size, typeof(TProtocol), path, factory));
        }

        public List<MultiaddressProtocol> Protocols { get; }

        public Multiaddress()
        {
            Protocols = new List<MultiaddressProtocol>();
        }

        public Multiaddress Add<TProtocol>(object value)
            where TProtocol : MultiaddressProtocol
        {
            var proto = _protocols.SingleOrDefault(p => p.Type == typeof(TProtocol));
            Protocols.Add(proto.Factory(value));
            return this;
        }

        public Multiaddress Add<TProtocol>() where TProtocol : MultiaddressProtocol => Add<TProtocol>(null);

        public Multiaddress Add(params MultiaddressProtocol[] protocols)
        {
            Protocols.AddRange(protocols);
            return this;
        }

        public TProtocol Get<TProtocol>() where TProtocol : MultiaddressProtocol => Protocols.OfType<TProtocol>().SingleOrDefault();
        public MultiaddressProtocol Get(Type multiprotocolType) => Protocols.Where(p => p.GetType() == multiprotocolType).SingleOrDefault();

        public void Remove<TProtocol>() where TProtocol : MultiaddressProtocol
        {
            var protocol = Get<TProtocol>();
            if (protocol != null)
                Protocols.Remove(protocol);
        }

        private static bool SupportsProtocol(string name) => _protocols.Any(p => p.Name.Equals(name));
        private static bool SupportsProtocol(int code) => _protocols.Any(p => p.Code.Equals(code));

        private static MultiaddressProtocol CreateProtocol(string name) => _protocols.SingleOrDefault(p => p.Name == name)?.Factory(null);
        private static MultiaddressProtocol CreateProtocol(int code) => _protocols.SingleOrDefault(p => p.Code == code)?.Factory(null);

        public static Multiaddress Decode(string value) => new Multiaddress().Add(DecodeProtocols(value.Split(new [] { '/' }, StringSplitOptions.RemoveEmptyEntries)).ToArray());
        public static Multiaddress Decode(byte[] bytes) => new Multiaddress().Add(DecodeProtocols(bytes).ToArray());

        private static IEnumerable<MultiaddressProtocol> DecodeProtocols(params string[] parts)
        {
            for (var i = 0; i < parts.Length; i++)
            {
                if (!SupportsProtocol(parts[i]))
                    throw new NotSupportedException(parts[i]);

                var protocol = CreateProtocol(parts[i]);
                if (protocol.Size != 0)
                {
                    if (i + 1 >= parts.Length)
                        throw new Exception("Required parameter not found");

                    if (_protocols.SingleOrDefault(p => p.Code == protocol.Code).Path)
                    {
                        protocol.Decode(string.Join("/", parts.Slice(i + 1)));
                        i = parts.Length - 1;
                    }
                    else
                    {
                        protocol.Decode(parts[++i]);
                    }
                }

                yield return protocol;
            }
        }

        private static IEnumerable<MultiaddressProtocol> DecodeProtocols(byte[] bytes)
        {
            var offset = 0;
            short code = 0;
            MultiaddressProtocol protocol = null;
            while (offset < bytes.Length)
            {
                offset += ParseProtocolCode(bytes, offset, out code);
                if (SupportsProtocol(code))
                {
                    offset += ParseProtocol(bytes, offset, code, out protocol);

                    yield return protocol;
                }
            }
        }

        private static int ParseProtocol(byte[] bytes, int offset, short code, out MultiaddressProtocol protocol)
        {
            var start = offset;
            protocol = CreateProtocol(code);
            offset += DecodeProtocol(protocol, bytes, offset);
            return offset - start;
        }

        private static int ParseProtocolCode(byte[] bytes, int offset, out short code)
        {
            code = Binary.LittleEndian.GetInt16(bytes, offset);
            return 2;
        }

        private static int DecodeProtocol(MultiaddressProtocol protocol, byte[] bytes, int offset)
        {
            int start = offset;
            int count = 0;
            if (protocol.Size > 0)
            {
                count = protocol.Size/8;
            }
            else if (protocol.Size == -1)
            {
                uint proxy = 0;
                offset += Binary.Varint.Read(bytes, offset, out proxy);
                count = (int) proxy;
            }

            if (count > 0)
            {
                protocol.Decode(bytes.Slice(offset, count));
                offset += count;
            }

            return offset - start;
        }

        public override string ToString() => Protocols.Count > 0 ? "/" + string.Join("/", Protocols.SelectMany(ProtocolToStrings)) : string.Empty;

        private static IEnumerable<string> ProtocolToStrings(MultiaddressProtocol p)
        {
            yield return p.Name;
            if (p.Value != null)
                yield return p.Value.ToString();
        }

        public byte[] ToBytes() => Protocols.SelectMany(EncodeProtocol).ToArray();

        private static IEnumerable<byte> EncodeProtocol(MultiaddressProtocol p)
        {
            var code = Binary.Varint.GetBytes((ulong)p.Code);

            if (p.Size == 0)
                return code;

            var bytes = p.ToBytes();

            if (p.Size > 0)
                return code.Concat(bytes);

            var prefix = Binary.Varint.GetBytes((ulong)bytes.Length);

            return code.Concat(prefix).Concat(bytes);
        }

        public IEnumerable<Multiaddress> Split() => Protocols.Select(p => new Multiaddress().Add(p));

        public static Multiaddress Join(IEnumerable<Multiaddress> addresses)
        {
            var result = new Multiaddress();
            foreach (var address in addresses)
            {
                result.Add(address.Protocols.ToArray());
            }
            return result;
        }

        public Multiaddress Encapsulate(Multiaddress address)
        {
            return new Multiaddress()
                .Add(Protocols.Concat(address.Protocols).ToArray());
        }

        public Multiaddress Decapsulate(Multiaddress address)
        {
            return new Multiaddress()
                .Add(Protocols.TakeWhile(p => !address.Protocols.Any(p.Equals)).ToArray());
        }

        public override bool Equals(object obj) => Equals((Multiaddress)obj);
        public bool Equals(Multiaddress other) => other != null && ToBytes().SequenceEqual(other.ToBytes());

        public static implicit operator Multiaddress(string value)
        {
            return Decode(value);
        }

        public bool Has<T>() where T : MultiaddressProtocol
            => Protocols.OfType<T>().Any();

        public Multiaddress Replace<T>(object v) where T : MultiaddressProtocol
        {
            Remove<T>();
            var protocolDef = _protocols.SingleOrDefault(p => p.Type == typeof(T));
            return Add(protocolDef.Factory(v));
        }
    }
}
