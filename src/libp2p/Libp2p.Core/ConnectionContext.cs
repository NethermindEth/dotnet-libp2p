// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Libp2p.Core;

//public class ConnectionContext(LocalPeer localPeer, ITransportProtocol transportProtocol) : ITransportConnectionContext
//{

//    public CancellationToken Token => throw new NotImplementedException();

//    public string Id => throw new NotImplementedException();

//    public IEnumerable<IProtocol> SubProtocols => throw new NotImplementedException();

//    public Identity Identity => throw new NotImplementedException();

//    public ISessionContext CreateSession()
//    {
//        throw new NotImplementedException();
//    }

//    public void Dispose()
//    {
//        throw new NotImplementedException();
//    }

//    public IChannel SubDial(IChannelRequest? request = null)
//    {
//        throw new NotImplementedException();
//    }

//    public Task SubDialAndBind(IChannel parentChannel, IChannelRequest? request = null)
//    {
//        throw new NotImplementedException();
//    }

//    public IChannel SubListen(IChannelRequest? request = null)
//    {
//        throw new NotImplementedException();
//    }

//    public Task SubListenAndBind(IChannel parentChannel, IChannelRequest? request = null)
//    {
//        throw new NotImplementedException();
//    }
//}