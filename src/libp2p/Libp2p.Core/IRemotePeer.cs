// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IRemotePeer : IPeer
{
    Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IId, IDialer;
    Task<TResult> DialAsync<TProtocol, TResult, TParams>(TParams @params, CancellationToken token = default)
        where TProtocol : IId, IDialer<TResult, TParams>;
    Task<TResult> DialAsync<TProtocol, TResult>(CancellationToken token = default)
        where TProtocol : IId, IDialer<TResult>;
    Task DisconnectAsync();
}
