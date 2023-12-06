using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Multiformats.Address.Protocols;

namespace Multiformats.Address.Net
{
    public static class MultiaddressExtensions
    {
        public static Multiaddress GetLocalMultiaddress(this Socket socket) => socket.LocalEndPoint.ToMultiaddress(socket.ProtocolType);
        public static Multiaddress GetRemoteMultiaddress(this Socket socket) => socket.RemoteEndPoint.ToMultiaddress(socket.ProtocolType);

        public static Multiaddress ToMultiaddress(this EndPoint ep, ProtocolType protocolType)
        {
            var ma = new Multiaddress();

            var ip = (IPEndPoint) ep;
            if (ip != null)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    ma.Add<IP4>(ip.Address);
                if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                    ma.Add<IP6>(ip.Address);

                if (protocolType == ProtocolType.Tcp)
                    ma.Add<TCP>((ushort) ip.Port);
                if (protocolType == ProtocolType.Udp)
                    ma.Add<UDP>((ushort) ip.Port);
            }

            return ma;
        }

        public static Multiaddress ToMultiaddress(this IPAddress ip)
        {
            var ma = new Multiaddress();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                ma.Add<IP4>(ip);
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                ma.Add<IP6>(ip);
            return ma;
        }

        public static IPEndPoint ToEndPoint(this Multiaddress ma)
        {
            ProtocolType pt;
            return ToEndPoint(ma, out pt);
        }

        public static IPEndPoint ToEndPoint(this Multiaddress ma, out ProtocolType protocolType)
        {
            SocketType st;
            return ToEndPoint(ma, out protocolType, out st);
        }

        public static IPEndPoint ToEndPoint(this Multiaddress ma, out ProtocolType protocolType, out SocketType socketType)
        {
            IPAddress addr = null;
            IP ip = ma.Protocols.OfType<IP4>().SingleOrDefault();
            if (ip != null)
                addr = (IPAddress) ip.Value;
            else
            {
                ip = ma.Protocols.OfType<IP6>().SingleOrDefault();
                if (ip != null)
                    addr = (IPAddress) ip.Value;
            }

            int? port = null;
            Number n = ma.Protocols.OfType<TCP>().SingleOrDefault();
            if (n != null)
            {
                port = (ushort) n.Value;
                protocolType = ProtocolType.Tcp;
                socketType = SocketType.Stream;
            }
            else
            {
                n = ma.Protocols.OfType<UDP>().SingleOrDefault();
                if (n != null)
                {
                    port = (ushort) n.Value;
                    protocolType = ProtocolType.Udp;
                    socketType = SocketType.Dgram;
                }
                else
                {
                    protocolType = ProtocolType.Unknown;
                    socketType = SocketType.Unknown;
                }
            }

            return new IPEndPoint(addr ?? IPAddress.Any, port ?? 0);
        }

        public static Socket CreateSocket(this Multiaddress ma)
        {
            IPEndPoint ep;
            return CreateSocket(ma, out ep);
        }

        public static Socket CreateSocket(this Multiaddress ma, out IPEndPoint ep)
        {
            ProtocolType pt;
            SocketType st;
            ep = ma.ToEndPoint(out pt, out st);

            return new Socket(ep.AddressFamily, st, pt);
        }

        public static Socket CreateConnection(this Multiaddress ma)
        {
            IPEndPoint ep;
            var socket = CreateSocket(ma, out ep);
            socket.Connect(ep);
            return socket;
        }

        public static Task<Socket> CreateConnectionAsync(this Multiaddress ma)
        {
            IPEndPoint ep;
            var socket = CreateSocket(ma, out ep);

#if NETSTANDARD1_6
            return socket.ConnectAsync(ep)
                .ContinueWith(_ => socket);
#else
            var tcs = new TaskCompletionSource<Socket>(); 
 
            try 
            { 
                socket.BeginConnect(ep, ar => 
                { 
                    try 
                    { 
                        socket.EndConnect(ar); 
                        tcs.TrySetResult(socket); 
                    } 
                    catch (Exception e) 
                    { 
                        tcs.TrySetException(e); 
                    } 
                }, null); 
            } 
            catch (Exception e) 
            { 
                tcs.TrySetException(e); 
            } 
 
            return tcs.Task; 
#endif
        }

        public static Socket CreateListener(this Multiaddress ma, int backlog = 10)
        {
            IPEndPoint ep;
            var socket = CreateSocket(ma, out ep);
            socket.Bind(ep);
            socket.Listen(backlog);
            return socket;
        }

        public static bool IsThinWaist(this Multiaddress ma)
        {
            if (!ma.Protocols.Any())
                return false;

            if (!(ma.Protocols[0] is IP4) && !(ma.Protocols[0] is IP6))
                return false;

            if (ma.Protocols.Count == 1)
                return true;

            return ma.Protocols[1] is TCP || ma.Protocols[1] is UDP ||
                   ma.Protocols[1] is IP4 || ma.Protocols[1] is IP6;
        }

        public static IEnumerable<Multiaddress> GetMultiaddresses(this NetworkInterface nic)
        {
            return nic
                .GetIPProperties()
                .UnicastAddresses
                .Select(addr => addr.Address.ToMultiaddress());
        }

        public static IEnumerable<Multiaddress> Match(this Multiaddress match, params Multiaddress[] addrs)
        {
            foreach (var a in addrs.Where(x => match.Protocols.Count == x.Protocols.Count))
            {
                var i = 0;

                if (a.Protocols.All(p2 => match.Protocols[i++].Code == p2.Code))
                    yield return a;
            }
        }
    }
}