namespace RandomNamespace
open System
open System.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Nethermind.Libp2p.Core
open Multiformats.Address
open Nethermind.Libp2p
open Nethermind.Libp2p.Core.Discovery
open System.Security.Cryptography
module Sample =
   module Protocol =
      ///<summary>
      /// simple symmetric protocol
      /// arg/_topic(a string for peers to recognize); arg/_ports(port expected by peers to listen on) arg/handler(handles after connection established successfully and communication initiated)
      ///</summary>
      type SmallTalk (_topic, _ports :uint16, handler) =
         inherit SymmetricSessionProtocol()
         let cts = new CancellationTokenSource()
         member _.topic = _topic
         member _.ports = _ports
         override x.ConnectAsync (channel, context, IsListener) = task { do! handler cts channel context IsListener |> Async.StartAsTask }
         //ISessionProtocol would be the interface required by ILibp2pPeerFactoryBuilder for handling the communication
         interface ISessionProtocol with
            member _.Id = _topic
            member x.DialAsync (downChannel: IChannel, context: ISessionContext): Task = 
               (x :> SymmetricSessionProtocol).DialAsync(downChannel, context)
            member x.ListenAsync (downChannel: IChannel, context: ISessionContext): Task = 
               (x :> SymmetricSessionProtocol).ListenAsync(downChannel, context)

   module Types =
      type [<Struct>] IpAddress =
      | IPv4 of string :string
      | IPv6 of string :string
      type Hashing<'T> (hasher :'T -> Async<byte array>) = member x.cal (token :'T) = hasher token
      type Conversation<'IdentityTypes> (protocol :Protocol.SmallTalk, hasher :Hashing<'IdentityTypes>, ?onNewPeers :Action<Multiaddress array>) =
         let libp2p_services =
            ServiceCollection().AddLibp2p(fun (builder :ILibp2pPeerFactoryBuilder) ->
               builder
                  .WithQuic()
                  .AddProtocol(protocol)
            ).BuildServiceProvider()
         let libp2p_identity_service_peers_generator = libp2p_services.GetRequiredService<IPeerFactory>()
         //PeerStore keeps record for discovered peers
         let libP2p_PeerStore = PeerStore()
         do
            if onNewPeers.IsSome then libP2p_PeerStore.add_OnNewPeer onNewPeers.Value
         member x.Peer (identity :'IdentityTypes) =
            async {
               let! identity = hasher.cal identity
               let created = libp2p_identity_service_peers_generator.Create(Identity(identity))
               return created
            }
         member _.multiAddressTemplate (ip_address :IpAddress, ports :uint16 option, ?peerid :PeerId) =
            let peerid_append = if peerid.IsSome then $"/p2p/{peerid.Value.ToString()}" else ""
            let ip_address = match ip_address with | IPv4 ip_address -> $"/ip4/{ip_address}" | IPv6 ip_address -> $"/ip6/{ip_address}"
            struct {|
               udp = Multiaddress.Decode($"{ip_address}/udp/{ports |> Option.defaultValue protocol.ports}/quic-v1{peerid_append}")
               tcp = Multiaddress.Decode($"{ip_address}/tcp/{ports |> Option.defaultValue protocol.ports}{peerid_append}")
            |}
         ///<summary>
         /// Apparently extracting peerinfos inside multiaddress and storing it and if it's new triggers internal event
         /// (https://github.com/NethermindEth/dotnet-libp2p/blob/a3d1c5802eac6ccc1c20b7cfc1412fd9c72d7eaf/src/libp2p/Libp2p.Core/Discovery/PeerStore.cs#L40).
         /// Method 'track' already automatically recording remote peerinfos
         ///</summary>
         member x.track (swarm :Multiaddress array) = libP2p_PeerStore.Discover swarm
         //if execution failed exception is returned as option
         member x.reach(local :'IdentityTypes, remote :'IdentityTypes, remote_ip :IpAddress, ?remote_ports :uint16, ?local_ports :uint16) =
            async {
               try
                  let! local_machine  = x.Peer local
                  let! remote_machine = x.Peer remote
                  let remote_address = x.multiAddressTemplate(remote_ip, remote_ports, remote_machine.Identity.PeerId)
                  let! session = local_machine.DialAsync [|remote_address.tcp; remote_address.udp|] |> Async.AwaitTask
                  //stores a successfully dialed remote so later simply reaching it without addressing is possible
                  libP2p_PeerStore.Discover [|session.RemoteAddress|]
                  do! session.DialAsync<Protocol.SmallTalk>() |> Async.AwaitTask
                  //Disconnect and I can dial again later
                  do! session.DisconnectAsync() |> Async.AwaitTask
                  return ValueNone
               with e -> return ValueSome e
            }
         //reaching peers assuming already know how to, returns ValueOption containing exception if encountered
         member x.reach(local :'IdentityTypes, remote :'IdentityTypes) =
            async {
               try
                  let! local_machine  = x.Peer local
                  let! remote_machine = x.Peer remote
                  let remote_infos = libP2p_PeerStore.GetPeerInfo remote_machine.Identity.PeerId
                  let! session = local_machine.DialAsync(remote_infos.Addrs.ToArray()) |> Async.AwaitTask
                  do! session.DialAsync<Protocol.SmallTalk>() |> Async.AwaitTask
                  //Disconnect and I can dial again later
                  do! session.DisconnectAsync() |> Async.AwaitTask
                  return ValueNone
               with e -> return ValueSome e
            }
         interface IDisposable with
            member x.Dispose () =
               if onNewPeers.IsSome then libP2p_PeerStore.remove_OnNewPeer onNewPeers.Value
         
      
      let Run () =
         //different ports potentially works on various machines
         //try dotnet run --no-build a couple times and sometimes it works
         //can connect to a remote computer by modifying this function making sure it's listening on 0.0.0.0 and on whatever ports that are allowed
         async {



            //for example use the code instead on server
            #if false
            use manager = new Conversation<string>(Protocol.SmallTalk("my topic", 4001us, fun cancelationTokenSource iChannel iSessionContext IsListener -> async {
               //cancelationTokenSource was not utilized as it's an example but it's in the wrapper 'Conversation'
               Console.WriteLine(if IsListener then "Listening..." else "Speaking...")
            }), hashing)
            let! pretend_remote = manager.Peer("remote")
            //ipv6 not supported currently, modify 'multiAddressTemplate' and the places it's used for supporting it, because Libp2p can use ipv6 obviously
            let address_ = manager.multiAddressTemplate(IPv4 "0.0.0.0", None, pretend_remote.Identity.PeerId)
            do! pretend_remote.StartListenAsync [|address_.tcp; address_.udp|] |> Async.AwaitTask
            #endif



            //Basic hashing function
            let hashing = Hashing(fun (identity :string) -> async {return SHA3_256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))})
            //Conversation<'T> where T would be the identity type and you can hash a string here and use the result as the peer you are looking for
            //Probably not a great design
            //Internally it's giving the hashed result to Libp2p's Identity which takes an array of bytes, and I saw people hashing password or whatever for connecting to a specific computer, so it's a way to achieve it
            use manager = new Conversation<string>(Protocol.SmallTalk("my topic", 11111us, fun cancelationTokenSource iChannel iSessionContext IsListener -> async {
               //cancelationTokenSource was not utilized as it's an example but it's in the wrapper 'Conversation'
               //iChannel would be the tool for speaking to another computer
               //iSessionContext has a lot information on for example who is the computer you are speaking with
               //IsListener tells if you are reached out and is a listener (true) for the conversation or you are the speaker reaching out to another computer (false)
               Console.WriteLine(if IsListener then "Listening..." else "Speaking...")
            }), hashing)
            let! pretend_remote = manager.Peer("remote")
            let address_ = manager.multiAddressTemplate(IPv4 "127.0.0.6", None, pretend_remote.Identity.PeerId)
            do! pretend_remote.StartListenAsync [|address_.tcp; address_.udp|] |> Async.AwaitTask
            //11111 was specified in the Protocol.SmallTalk constructor and basically all connections would be using that port unless specified
            //meanwhile the problem was it semms if you are testing locally which we are and both construct of 'peers' are on 127.0.0.6 ip address and listening on the same ports it wouldn't work
            //therefore the number 11112 is assigned for local ports (in theory can use same port if one is listening on 127.0.0.6 and another is listening on 127.0.0.7)
            do! manager.reach("Im local", "my names remote", IPv4 "127.0.0.6", local_ports = 11112us) |> Async.Ignore
            do! manager.reach("Im local", "my names remote") |> Async.Ignore
            return ()
         }
//AI can translate codes into C# if required
