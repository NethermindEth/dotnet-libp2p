// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Reflection;
using System.Text;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.I2p;

public static class I2pMultiaddr
{
    private static readonly object RegisterLock = new();
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        lock (RegisterLock)
        {
            if (_registered)
            {
                return;
            }

            Type multiaddressType = typeof(Multiaddress);
            FieldInfo protocolsField = multiaddressType.GetField("_protocols", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Multiaddress protocol table was not found.");
            Type protocolType = multiaddressType.GetNestedType("Protocol", BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new InvalidOperationException("Multiaddress protocol descriptor type was not found.");
            ConstructorInfo protocolCtor = protocolType.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                [typeof(string), typeof(int), typeof(int), typeof(Type), typeof(bool), typeof(Func<object, MultiaddressProtocol>)],
                modifiers: null)
                ?? throw new InvalidOperationException("Multiaddress protocol descriptor constructor was not found.");

            IList protocols = (IList)(protocolsField.GetValue(null)
                ?? throw new InvalidOperationException("Multiaddress protocol table is unavailable."));

            AddProtocol(protocols, protocolCtor, "garlic64", Garlic64.CodeValue, typeof(Garlic64), value => new Garlic64(value));
            AddProtocol(protocols, protocolCtor, "garlic32", Garlic32.CodeValue, typeof(Garlic32), value => new Garlic32(value));
            _registered = true;
        }
    }

    public static bool IsI2pAddress(Multiaddress address)
    {
        Register();
        return address.Has<Garlic32>() || address.Has<Garlic64>();
    }

    public static string GetDestination(Multiaddress address)
    {
        Register();
        Garlic64? garlic64 = address.Get<Garlic64>();
        if (garlic64 is not null)
        {
            return garlic64.Destination;
        }

        Garlic32? garlic32 = address.Get<Garlic32>();
        if (garlic32 is not null)
        {
            return $"{garlic32.Destination}.b32.i2p";
        }

        throw new I2pException($"Address '{address}' does not contain garlic32 or garlic64.");
    }

    public static Multiaddress FromGarlic64(string destination, PeerId peerId)
    {
        Register();
        return new Multiaddress().Add(new Garlic64(destination)).Add<P2P>(peerId.ToString());
    }

    public static Multiaddress FromGarlic64(string destination)
    {
        Register();
        return new Multiaddress().Add(new Garlic64(destination));
    }

    private static void AddProtocol(IList protocols, ConstructorInfo protocolCtor, string name, int code, Type type, Func<object, MultiaddressProtocol> factory)
    {
        foreach (object protocol in protocols)
        {
            PropertyInfo? nameProperty = protocol.GetType().GetProperty("Name");
            if (string.Equals((string?)nameProperty?.GetValue(protocol), name, StringComparison.Ordinal))
            {
                return;
            }
        }

        protocols.Add(protocolCtor.Invoke([name, code, -1, type, false, factory]));
    }
}

public sealed class Garlic64 : I2pGarlicProtocol
{
    public const int CodeValue = 0x01be;

    public Garlic64()
        : base("garlic64", CodeValue)
    {
    }

    public Garlic64(object value)
        : this()
    {
        DecodeValue(value);
    }
}

public sealed class Garlic32 : I2pGarlicProtocol
{
    public const int CodeValue = 0x01bf;

    public Garlic32()
        : base("garlic32", CodeValue)
    {
    }

    public Garlic32(object value)
        : this()
    {
        DecodeValue(value);
    }
}

public abstract class I2pGarlicProtocol(string name, int code) : MultiaddressProtocol(name, code, -1)
{
    private const int Garlic64MinBytes = 387;
    private const int Garlic32HashBytes = 32;
    private const int Garlic32EncryptedMinBytes = 35;
    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    private byte[] _destinationBytes = [];

    public string Destination => Name switch
    {
        "garlic64" => EncodeGarlic64(_destinationBytes),
        "garlic32" => EncodeGarlic32(_destinationBytes),
        _ => throw new InvalidOperationException($"Unsupported garlic protocol: {Name}")
    };

    public ReadOnlySpan<byte> DestinationBytes => _destinationBytes;

    public override void Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('/'))
        {
            throw new FormatException($"{Name} destination cannot contain '/'.");
        }

        _destinationBytes = Name switch
        {
            "garlic64" => DecodeGarlic64(value),
            "garlic32" => DecodeGarlic32(value),
            _ => throw new InvalidOperationException($"Unsupported garlic protocol: {Name}")
        };
    }

    public override void Decode(byte[] data)
    {
        Validate(data);
        _destinationBytes = data.ToArray();
    }

    public override byte[] ToBytes()
    {
        return _destinationBytes.ToArray();
    }

    public override string ToString()
    {
        return Destination;
    }

    protected void DecodeValue(object value)
    {
        switch (value)
        {
            case null:
                break;
            case string text:
                Decode(text);
                break;
            case byte[] bytes:
                Decode(bytes);
                break;
            default:
                throw new ArgumentException($"Unsupported {Name} value type: {value.GetType().FullName}", nameof(value));
        }
    }

    private void Validate(byte[] bytes)
    {
        if (Name == "garlic64")
        {
            if (bytes.Length < Garlic64MinBytes)
            {
                throw new FormatException($"garlic64 destination must be at least {Garlic64MinBytes} bytes.");
            }

            return;
        }

        if (Name == "garlic32")
        {
            if (bytes.Length is not Garlic32HashBytes && bytes.Length < Garlic32EncryptedMinBytes)
            {
                throw new FormatException($"garlic32 destination must be {Garlic32HashBytes} bytes or at least {Garlic32EncryptedMinBytes} bytes.");
            }

            return;
        }

        throw new InvalidOperationException($"Unsupported garlic protocol: {Name}");
    }

    private byte[] DecodeGarlic64(string value)
    {
        string base64 = value.Replace('-', '+').Replace('~', '/');
        int remainder = base64.Length % 4;
        if (remainder == 1)
        {
            throw new FormatException("Invalid garlic64 destination length.");
        }
        if (remainder != 0)
        {
            base64 = base64.PadRight(base64.Length + 4 - remainder, '=');
        }

        byte[] bytes = Convert.FromBase64String(base64);
        Validate(bytes);
        return bytes;
    }

    private byte[] DecodeGarlic32(string value)
    {
        if (value.Contains('.') || value.Contains('='))
        {
            throw new FormatException("garlic32 destination must be raw lowercase base32 without .b32.i2p or padding.");
        }

        List<byte> bytes = [];
        int buffer = 0;
        int bits = 0;
        foreach (char c in value)
        {
            int digit = Base32Alphabet.IndexOf(char.ToLowerInvariant(c));
            if (digit < 0)
            {
                throw new FormatException($"Invalid garlic32 character '{c}'.");
            }

            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xff));
                buffer &= (1 << bits) - 1;
            }
        }

        if (bits > 0 && ((buffer << (8 - bits)) & 0xff) != 0)
        {
            throw new FormatException("Invalid garlic32 trailing bits.");
        }

        byte[] result = [.. bytes];
        Validate(result);
        return result;
    }

    private static string EncodeGarlic64(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '~');
    }

    private static string EncodeGarlic32(ReadOnlySpan<byte> bytes)
    {
        StringBuilder builder = new((bytes.Length * 8 + 4) / 5);
        int buffer = 0;
        int bits = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Base32Alphabet[(buffer >> bits) & 0x1f]);
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1f]);
        }

        return builder.ToString();
    }
}
