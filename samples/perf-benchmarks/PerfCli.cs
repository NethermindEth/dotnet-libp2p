// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace DataTransferBenchmark;

public sealed record PerfArgs(bool RunServer, string? ServerAddress, string Transport, ulong UploadBytes, ulong DownloadBytes)
{
    public static bool IsCliMode(string[] args) => args.Any(a => a.StartsWith("--", StringComparison.Ordinal));

    public static PerfArgs Parse(string[] args)
    {
        bool server = false;
        string? address = null;
        string transport = "tcp";
        ulong upload = 0, download = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--run-server": server = true; break;
                case "--server-address": address = Value(args, ref i); break;
                case "--transport": transport = Value(args, ref i); break;
                case "--upload-bytes": upload = ulong.Parse(Value(args, ref i)); break;
                case "--download-bytes": download = ulong.Parse(Value(args, ref i)); break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (!server && string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("--server-address is required in client mode");

        return new PerfArgs(server, address, transport, upload, download);
    }

    private static string Value(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} requires a value");
}

public static class PerfCli
{
    public static async Task<int> RunAsync(IPeerFactory peerFactory, ILogger logger, PerfArgs args)
    {
        if (args.Transport is not ("tcp" or "quic" or "quic-v1"))
        {
            logger.LogError("Unsupported transport {Transport}. Supported: tcp, quic, quic-v1", args.Transport);
            return 1;
        }

        bool quic = args.Transport != "tcp";
        var peer = peerFactory.Create();

        try
        {
            if (args.RunServer)
            {
                Multiaddress listen = ToMultiaddress(args.ServerAddress ?? "0.0.0.0:4001", quic);
                await peer.StartListenAsync(new[] { listen });
                logger.LogInformation("Listening on {Addr}", listen);
                await Task.Delay(Timeout.Infinite); // the runner kills the server
                return 0;
            }

            Multiaddress target = ToMultiaddress(args.ServerAddress!, quic);

            using var reporter = new PerfReporter(); // start the clock before dialing
            PerfProtocol.Reporter = reporter;
            PerfProtocol.BytesToSend = args.UploadBytes;
            PerfProtocol.BytesToReceive = args.DownloadBytes;

            string localListen = quic ? "/ip4/0.0.0.0/udp/0/quic-v1" : "/ip4/0.0.0.0/tcp/0";
            await peer.StartListenAsync(new[] { Multiaddress.Decode(localListen) });

            ISession session = await peer.DialAsync(target);
            await session.DialAsync<PerfProtocol>();

            reporter.Final();
            await session.DisconnectAsync();
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError("Perf run failed: {Error}", ex.ToString());
            return 1;
        }
    }

    /// <summary>Accepts a full multiaddr ("/ip4/..") or "host:port".</summary>
    private static Multiaddress ToMultiaddress(string address, bool quic)
    {
        if (address.StartsWith('/'))
            return Multiaddress.Decode(address);

        int colon = address.LastIndexOf(':');
        if (colon <= 0) throw new ArgumentException($"Invalid server address: {address}");

        string host = address[..colon];
        string port = address[(colon + 1)..];
        string ipProto = IPAddress.TryParse(host, out _) ? "ip4" : "dns4";
        string text = quic
            ? $"/{ipProto}/{host}/udp/{port}/quic-v1"
            : $"/{ipProto}/{host}/tcp/{port}";
        return Multiaddress.Decode(text);
    }
}