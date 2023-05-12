//// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
//// SPDX-License-Identifier: MIT

//extern alias BouncyCastleCryptography;
//using Google.Protobuf;
//using Microsoft.Extensions.Logging;
//using Nethermind.Libp2p.Core;
//using Nethermind.Libp2p.Protocols.GossipSub.Dto;
//using Org.BouncyCastle.Math.EC.Rfc8032;
//using System.Text;
//using System.Buffers.Binary;

//namespace Nethermind.Libp2p.Protocols;

///// <summary>
/////     https://github.com/multiformats/multistream-select
///// </summary>
//public class GossipsubProtocol : IProtocol
//{
//    private readonly ILogger? _logger;
//    public string Id => "/meshsub/1.0.0";
//    private const string PayloadSigPrefix = "libp2p-pubsub:";
//    ulong seqNo = 1;



//    public GossipsubProtocol(ILoggerFactory? loggerFactory = null)
//    {
//        _logger = loggerFactory?.CreateLogger<GossipsubProtocol>();
//    }

//    public async Task DialAsync(IChannel channel, IChannelFactory channelFactory,
//        IPeerContext context)
//    {
//        SendIHave += (topic, message) =>
//        {
//            ulong seq = seqNo++;
//            Messages[seq] = (topic, message);

//            Rpc rpc = new Rpc();
//            _logger?.LogDebug("Sending a message {0}", message);
//            Span<byte> seqNoBytes = new byte[8];
//            BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);

//            ControlIHave iHave = new ControlIHave()
//            {
//                TopicID = topic
//            };

//            iHave.MessageIDs.Add(ByteString.CopyFrom(seqNoBytes));
//            rpc.Control = new ControlMessage();
//            rpc.Control.Ihave.Add(iHave);
//            ulong id = seq;
//            WithMessages(rpc, Messages[id].topic, id, context.LocalPeer.Identity.PeerIdBytes, Messages[id].message, context.LocalPeer.Identity.PrivateKey);

//            _ = channel.WritePrefixedProtobufAsync(rpc);
//        };

//        Send += (ulong[] ids) =>
//        {
//            Rpc rpc = new Rpc();
//            foreach (ulong id in ids)
//            {
//                if (Messages.ContainsKey(id))
//                {
//                    WithMessages(rpc, Messages[id].topic, id, context.LocalPeer.Identity.PeerIdBytes, Messages[id].message, context.LocalPeer.Identity.PrivateKey);
//                }

//            }
//            if (rpc.Publish.Any())
//            {
//                _ = channel.WritePrefixedProtobufAsync(rpc);
//            }
//        };

//        SendIWant += (ByteString[] ids) =>
//        {
//            Rpc rpc = new Rpc();
//            _logger?.LogDebug($"Sending iwant");
//            Span<byte> seqNoBytes = new byte[8];
//            BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);

//            ControlIWant iWant = new ControlIWant()
//            {
//            };

//            iWant.MessageIDs.AddRange(ids);
//            rpc.Control = new ControlMessage();
//            rpc.Control.Iwant.Add(iWant);
//            _ = channel.WritePrefixedProtobufAsync(rpc);
//            _logger?.LogDebug($"Requesting via msgs IWant");

//        };

//        Rpc rpc = new Rpc();
//        rpc.Subscriptions.AddRange(Topics.Select(s =>
//        {
//            Rpc.Types.SubOpts res = new Rpc.Types.SubOpts();
//            res.Topicid = s.Key;
//            res.Subscribe = true;
//            return res;
//        }));
//        await channel.WritePrefixedProtobufAsync(rpc);

//            //rpc = new Rpc();
//            //rpc.Control = new ControlMessage();
//            //rpc.Control.Graft.AddRange(Topics.Select(s =>
//            //{
//            //    var res = new ControlGraft();
//            //    res.TopicID = s.Key;
//            //    return res;
//            //}));
//            //await channel.WritePrefixedProtobufAsync(rpc);

//        await channel;
//        _logger?.LogDebug($"Gossipsub dial completed");

//    }

//    public async Task ListenAsync(IChannel channel, IChannelFactory channelFactory,
//        IPeerContext context)
//    {
//        try
//        {
//            _logger?.LogDebug($"Gossipsub list connected");
//            while (true)
//            {
//                Rpc? rpc = await channel.ReadPrefixedProtobufAsync(Rpc.Parser);

//                _logger?.LogDebug($"{rpc} {rpc.Publish.Count}");
//                foreach (Message? msg in rpc.Publish)
//                {

//                    string msgStr = Encoding.UTF8.GetString(msg.Data.ToByteArray());
//                    _logger?.LogDebug($"Msg: {msgStr} {BitConverter.ToInt64(msg.Seqno.ToByteArray())} {msg}");
//                    Message msgToBeVerified = msg.Clone();
//                    msgToBeVerified.ClearSignature();

//                    byte[] msgToSign = Encoding.UTF8.GetBytes(PayloadSigPrefix)
//                      .Concat(msgToBeVerified.ToByteArray())
//                      .ToArray();
//                    bool isValid = Ed25519.Verify(msg.Signature.ToByteArray(), 0, context.RemotePeer.Identity.PublicKey.Data.ToByteArray(), 0, msgToSign, 0, msgToSign.Length);
//                    if (!isValid)
//                    {
//                        _logger?.LogWarning("Unable to verify msg signature");
//                    }
//                    else
//                    {
//                        _logger?.LogDebug("The signature is correct");
//                    }

//                    if (Topics.ContainsKey(msg.Topic))
//                    {
//                        Topics[msg.Topic].topic.onMessage?.Invoke(msg.Data.ToByteArray());
//                    }
//                    else
//                    {
//                        _logger?.LogWarning("Unknown topic");
//                    }
//                }

//                if (rpc?.Control?.Iwant is not null)
//                {
//                    List<ulong> toSend = new List<ulong>();
//                    foreach (ControlIWant? iwant in rpc.Control.Iwant)
//                    {
//                        foreach (ByteString? id in iwant.MessageIDs)
//                        {
//                            ulong idUlong = BinaryPrimitives.ReadUInt64BigEndian(id.ToByteArray());
//                            if (Messages.ContainsKey(idUlong))
//                            {
//                                toSend.Add(idUlong);
//                            }
//                        }
//                    }
//                    if (toSend.Any())
//                    {
//                        Send?.Invoke(toSend.ToArray());
//                    }
//                }
//                if (rpc?.Control?.Ihave is not null && rpc.Control.Ihave.Any())
//                {
//                    List<ByteString> toSendIWant = new List<ByteString>();
//                    foreach (ControlIHave? ihave in rpc.Control.Ihave)
//                    {
//                        foreach (ByteString? id in ihave.MessageIDs)
//                        {
//                            toSendIWant.Add(id);
//                        }
//                    }

//                    if (toSendIWant.Any())
//                    {
//                        SendIWant?.Invoke(toSendIWant.ToArray());
//                    }
//                }

//                if (rpc?.Control?.Graft is not null && rpc.Control.Graft.Any())
//                {

//                }
//                if (rpc?.Control?.Prune is not null && rpc.Control.Prune.Any())
//                {

//                }
//            }
//        }
//        catch
//        {

//        }
//    }

//    public delegate void SendHandler(string topic, byte[] message);
//    public delegate void SendIWantHandler(ByteString[] ids);
//    public delegate void SendIHaveHandler(ulong[] ids);

//    public static SendHandler SendIHave;
//    public static SendIWantHandler SendIWant;
//    public static SendIHaveHandler Send;

//    public static void WithMessages(Rpc rpc, string topic, ulong seqNo, byte[] from, byte[] message, byte[] privateKey)
//    {
//        Message msg = new Message();
//        msg.Topic = topic;
//        Span<byte> seqNoBytes = new byte[8];
//        BinaryPrimitives.WriteUInt64BigEndian(seqNoBytes, seqNo);
//        msg.Seqno = ByteString.CopyFrom(seqNoBytes);
//        msg.From = ByteString.CopyFrom(from);
//        msg.Data = ByteString.CopyFrom(message);

//        byte[] msgToSign = Encoding.UTF8.GetBytes(PayloadSigPrefix)
//            .Concat(msg.ToByteArray())
//            .ToArray();
//        byte[] sig = new byte[64];
//        Ed25519.Sign(privateKey, 0, msgToSign, 0, msgToSign.Length, sig, 0);

//        msg.Signature = ByteString.CopyFrom(sig);
//        rpc.Publish.Add(msg);
//    }
//}
