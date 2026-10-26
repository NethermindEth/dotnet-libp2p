// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Generators.Protobuf;

[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class ProtocLocationAttribute : Attribute
{
    public ProtocLocationAttribute(string protocLocation)
    {
        ProtocLocation = protocLocation;
    }

    public string ProtocLocation { get; }
}
