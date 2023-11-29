using System.Text;
using Multiformats.Hash;

namespace Multiformats.Address.Protocols
{
    public class DNS6 : MultiaddressProtocol
    {
        public DNS6()
            : base("dns6", 55, -1)
        {
        }

        public DNS6(string address)
            : this()
        {
            Value = address;
        }

        public override void Decode(string value) => Value = value;
        public override void Decode(byte[] bytes) => Value = Encoding.UTF8.GetString(bytes);
        public override byte[] ToBytes() => Encoding.UTF8.GetBytes((string)Value);
        public override string ToString() => (string)Value ?? string.Empty;
    }
}
