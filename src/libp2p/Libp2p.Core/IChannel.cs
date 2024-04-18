// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

public interface IChannel : IReader, IWriter
{
    ValueTask CloseAsync();
    TaskAwaiter GetAwaiter();
}
