// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Core;

public interface IPeer
{
    Identity Identity { get; }

    Task<ISession> DialAsync(Multiaddress addr, CancellationToken token = default);

    Task StartListenAsync(Multiaddress[] addrs, CancellationToken token = default);

    //Task DisconnectAsync();

    ObservableCollection<Multiaddress> ListenAddresses { get; }

    //event OnConnection OnConnection;
}

public delegate Task OnConnection(ISession peer);

public interface ISession
{

}
