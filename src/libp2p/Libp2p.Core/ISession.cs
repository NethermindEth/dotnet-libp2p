// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public interface ISession
{
    Multiaddress RemoteAddress { get; }
    Activity? Activity { get; }

    Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol;
    Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>;
    Task DisconnectAsync();
}
