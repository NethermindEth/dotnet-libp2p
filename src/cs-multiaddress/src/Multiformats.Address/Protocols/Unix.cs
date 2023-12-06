using System;
using System.Linq;
using System.Text;
using BinaryEncoding;

namespace Multiformats.Address.Protocols
{
    public class Unix : MultiaddressProtocol
    {
        public string Path => Value != null ? (string) Value : string.Empty;

        public Unix()
            : base("unix", 400, -1)
        {
        }

        public Unix(string address)
            : this()
        {
            Value = address;
        }

        public override void Decode(string value)
        {
            Value = value;
        }

        public override void Decode(byte[] bytes)
        {
            uint size = 0;
            var n = Binary.Varint.Read(bytes, 0, out size);

            if (bytes.Length - n != size)
                throw new Exception("Inconsitent lengths");

            if (size == 0)
                throw new Exception("Invalid length");

            var s = Encoding.UTF8.GetString(bytes, n, bytes.Length - n);

            Value = s.Substring(1);
        }

        public override byte[] ToBytes()
        {
            return Binary.Varint.GetBytes((uint) Encoding.UTF8.GetByteCount((string) Value))
                .Concat(Encoding.UTF8.GetBytes((string) Value)).ToArray();
        }
    }
}
