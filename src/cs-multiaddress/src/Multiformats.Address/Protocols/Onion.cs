using System;
using System.Linq;
using BinaryEncoding;
using Multiformats.Base;

namespace Multiformats.Address.Protocols
{
    public class Onion : MultiaddressProtocol
    {
        public string Address => Value != null ? (string) Value : string.Empty;

        public Onion()
            : base("onion", 444, 96)
        {
        }

        public Onion(string s)
            : this()
        {
            Value = s;
        }

        public override void Decode(string value)
        {
            var addr = value.Split(':');
            if (addr.Length != 2)
                throw new Exception("Failed to parse addr");

            if (addr[0].Length != 16)
                throw new Exception("Failed to parse addr");

            if (!Multibase.TryDecode(addr[0], out var encoding, out _) || encoding != MultibaseEncoding.Base32Lower)
                throw new InvalidOperationException($"{value} is not a valid onion address.");

            var i = ushort.Parse(addr[1]);
            if (i < 1)
                throw new Exception("Failed to parse addr");

            Value = value;
        }

        public override void Decode(byte[] bytes)
        {
            var addr = Multibase.Base32.Encode(bytes.Slice(0, 10));
            var port = Binary.BigEndian.GetUInt16(bytes, 10);

            Value = $"{addr}:{port}";
        }

        public override byte[] ToBytes()
        {
            var s = (string) Value;
            var addr = s.Split(':');
            if (addr.Length != 2)
                throw new Exception("Failed to parse addr");

            if (addr[0].Length != 16)
                throw new Exception("Failed to parse addr");

            if (!Multibase.TryDecode(addr[0], out var encoding, out var onionHostBytes) || encoding != MultibaseEncoding.Base32Lower)
                throw new InvalidOperationException($"{s} is not a valid onion address.");

            var i = ushort.Parse(addr[1]);
            if (i < 1)
                throw new Exception("Failed to parse addr");

            return onionHostBytes.Concat(Binary.BigEndian.GetBytes(i)).ToArray();
        }
    }
}
