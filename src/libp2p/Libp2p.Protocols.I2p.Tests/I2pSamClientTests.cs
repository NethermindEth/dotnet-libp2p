// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Nethermind.Libp2p.Protocols.I2p.Tests;

public class I2pSamClientTests
{
    [Test]
    public void BuildCommands_UsePrimaryAndSubsessions()
    {
        I2pOptions options = new()
        {
            SessionId = "primary",
            StreamSessionId = "stream",
            DatagramSessionId = "datagram"
        };
        I2pSamClient client = new(options);

        string primary = Invoke<string>(client, "BuildPrimarySessionCreateCommand", ["TRANSIENT"]);
        string plainStream = Invoke<string>(client, "BuildStreamSessionCreateCommand", ["TRANSIENT"]);
        string stream = Invoke<string>(client, "BuildStreamSessionAddCommand", []);
        string datagram = Invoke<string>(client, "BuildDatagramSessionAddCommand", [new IPEndPoint(IPAddress.Loopback, 1234)]);

        Assert.That(primary, Does.StartWith("SESSION CREATE STYLE=MASTER ID=primary DESTINATION=TRANSIENT"));
        Assert.That(plainStream, Does.StartWith("SESSION CREATE STYLE=STREAM ID=stream DESTINATION=TRANSIENT"));
        Assert.That(stream, Is.EqualTo("SESSION ADD STYLE=STREAM ID=stream"));
        Assert.That(datagram, Is.EqualTo("SESSION ADD STYLE=DATAGRAM ID=datagram PORT=1234 sam.udp.host=127.0.0.1 sam.udp.port=7655"));
    }

    [Test]
    public void BuildDatagramSessionAddCommand_IncludesHostOnlyWhenExplicit()
    {
        I2pOptions options = new()
        {
            DatagramSessionId = "datagram",
            DatagramHost = "192.0.2.7"
        };
        I2pSamClient client = new(options);

        string datagram = Invoke<string>(client, "BuildDatagramSessionAddCommand", [new IPEndPoint(IPAddress.Any, 1234)]);

        Assert.That(datagram, Does.Contain(" HOST=192.0.2.7 "));
    }

    [Test]
    public async Task CreateDatagramSessionAsync_ReleasesUdpSocketWhenSamConnectFails()
    {
        using UdpClient udpProbe = new(new IPEndPoint(IPAddress.Loopback, 0));
        int udpPort = ((IPEndPoint)udpProbe.Client.LocalEndPoint!).Port;
        udpProbe.Dispose();

        TcpListener tcpProbe = new(IPAddress.Loopback, 0);
        tcpProbe.Start();
        int closedTcpPort = ((IPEndPoint)tcpProbe.LocalEndpoint).Port;
        tcpProbe.Stop();

        I2pSamClient client = new(new I2pOptions
        {
            SamPort = closedTcpPort,
            DatagramHost = IPAddress.Loopback.ToString(),
            DatagramPort = udpPort,
            ConnectTimeoutMilliseconds = 250
        });

        Assert.That(async () => await client.CreateDatagramSessionAsync(CancellationToken.None), Throws.Exception);

        using UdpClient rebound = new(new IPEndPoint(IPAddress.Loopback, udpPort));
        Assert.That(((IPEndPoint)rebound.Client.LocalEndPoint!).Port, Is.EqualTo(udpPort));
    }

    [Test]
    public async Task CreateSessionAsync_DefaultsToPlainStreamSession()
    {
        await using FakeSamRouter router = new();
        I2pOptions options = new()
        {
            SamPort = router.Port,
            StreamSessionId = "stream",
            UsePrimarySessionForStreams = false
        };
        I2pSamClient client = new(options);

        string destination = await client.CreateSessionAsync(CancellationToken.None);

        Assert.That(destination, Is.EqualTo(I2pTestDestinations.Garlic64()));
        Assert.That(router.Commands, Has.Some.EqualTo("HELLO VERSION MIN=3.1 MAX=3.3"));
        Assert.That(router.Commands, Has.Some.StartsWith("SESSION CREATE STYLE=STREAM ID=stream DESTINATION=TRANSIENT"));
        Assert.That(router.Commands.Any(static command => command.StartsWith("SESSION ADD ", StringComparison.Ordinal)), Is.False);
        await client.DisposeAsync();
    }

    [Test]
    public async Task CreateSessionAsync_UsesPrimaryStreamSubsessionWhenConfigured()
    {
        await using FakeSamRouter router = new();
        I2pOptions options = new()
        {
            SamPort = router.Port,
            SessionId = "primary",
            StreamSessionId = "stream",
            UsePrimarySessionForStreams = true
        };
        I2pSamClient client = new(options);

        string destination = await client.CreateSessionAsync(CancellationToken.None);

        Assert.That(destination, Is.EqualTo(I2pTestDestinations.Garlic64()));
        Assert.That(router.Commands, Has.Some.EqualTo("HELLO VERSION MIN=3.3 MAX=3.3"));
        Assert.That(router.Commands, Has.Some.StartsWith("SESSION CREATE STYLE=MASTER ID=primary DESTINATION=TRANSIENT"));
        Assert.That(router.Commands, Has.Some.EqualTo("SESSION ADD STYLE=STREAM ID=stream"));
        await client.DisposeAsync();
    }

    [Test]
    public async Task CreateDatagramSessionAsync_DoesNotPoisonClientWhenSamUdpEndpointIsInvalid()
    {
        await using FakeSamRouter router = new();
        I2pOptions options = new()
        {
            SamPort = router.Port,
            DatagramHost = IPAddress.Loopback.ToString(),
            SamUdpPort = -1
        };
        I2pSamClient client = new(options);

        Assert.That(async () => await client.CreateDatagramSessionAsync(CancellationToken.None), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(router.Commands.Any(static command => command.StartsWith("SESSION ADD STYLE=DATAGRAM", StringComparison.Ordinal)), Is.False);

        options.SamUdpPort = I2pOptions.DefaultSamUdpPort;
        await using I2pDatagramSession session = await client.CreateDatagramSessionAsync(CancellationToken.None);

        Assert.That(session.SessionId, Is.EqualTo(options.DatagramSessionId));
        await client.DisposeAsync();
    }

    [Test]
    public async Task DatagramSessionDispose_NoopsAfterClientDispose()
    {
        await using FakeSamRouter router = new();
        I2pSamClient client = new(new I2pOptions
        {
            SamPort = router.Port,
            DatagramHost = IPAddress.Loopback.ToString()
        });

        I2pDatagramSession session = await client.CreateDatagramSessionAsync(CancellationToken.None);

        await client.DisposeAsync();
        await session.DisposeAsync();
    }

    [Test]
    public void ConnectStreamAsync_RejectsCommandInjection()
    {
        I2pSamClient client = new(new I2pOptions());

        Assert.That(
            async () => await client.ConnectStreamAsync($"{I2pTestDestinations.Garlic64()}\nSESSION REMOVE ID=x", CancellationToken.None),
            Throws.InstanceOf<ArgumentException>());
    }

    private static T Invoke<T>(I2pSamClient client, string methodName, object?[] arguments)
    {
        MethodInfo method = typeof(I2pSamClient).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (T)method.Invoke(client, arguments)!;
    }

    private sealed class FakeSamRouter : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ConcurrentBag<Task> _clientTasks = [];
        private readonly Task _acceptTask;

        public FakeSamRouter()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }
        public ConcurrentQueue<string> Commands { get; } = new();

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
                    _clientTasks.Add(Task.Run(() => HandleClientAsync(client)));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                await using NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                await using StreamWriter writer = new(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                try
                {
                    while (!_cancellation.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                        if (line is null)
                        {
                            return;
                        }

                        Commands.Enqueue(line);
                        string response = line switch
                        {
                            string value when value.StartsWith("HELLO VERSION ", StringComparison.Ordinal) => "HELLO REPLY RESULT=OK VERSION=3.3",
                            string value when value.StartsWith("SESSION CREATE ", StringComparison.Ordinal) => "SESSION STATUS RESULT=OK",
                            "NAMING LOOKUP NAME=ME" => $"NAMING REPLY RESULT=OK NAME=ME VALUE={I2pTestDestinations.Garlic64()}",
                            string value when value.StartsWith("SESSION ADD ", StringComparison.Ordinal) => "SESSION STATUS RESULT=OK",
                            string value when value.StartsWith("SESSION REMOVE ", StringComparison.Ordinal) => "SESSION STATUS RESULT=OK",
                            _ => "SESSION STATUS RESULT=I2P_ERROR MESSAGE=\"unexpected command\""
                        };
                        await writer.WriteLineAsync(response).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                }
                catch (IOException) when (_cancellation.IsCancellationRequested)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }

            await Task.WhenAll(_clientTasks).ConfigureAwait(false);
            _cancellation.Dispose();
        }
    }
}
