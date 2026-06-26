// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Nethermind.Libp2p.Protocols.I2p;

namespace Nethermind.Libp2p.Protocols.I2p.Tests;

public class I2pSamResponseTests
{
    [Test]
    public void Parse_ExtractsTopicTypeAndValues()
    {
        I2pSamResponse response = I2pSamResponse.Parse("SESSION STATUS RESULT=OK DESTINATION=abc");

        Assert.That(response.Topic, Is.EqualTo("SESSION"));
        Assert.That(response.Type, Is.EqualTo("STATUS"));
        Assert.That(response.Result, Is.EqualTo("OK"));
        Assert.That(response.Values["DESTINATION"], Is.EqualTo("abc"));
    }

    [Test]
    public void ThrowIfNotOk_ThrowsForFailedResponse()
    {
        I2pSamResponse response = I2pSamResponse.Parse("STREAM STATUS RESULT=CANT_REACH_PEER MESSAGE=\"connect timeout\"");

        I2pException exception = Assert.Throws<I2pException>(() => response.ThrowIfNotOk("STREAM CONNECT"))!;

        Assert.That(exception.Message, Does.Contain("connect timeout"));
    }

    [Test]
    public void Parse_UnescapesQuotedValues()
    {
        I2pSamResponse response = I2pSamResponse.Parse("STREAM STATUS RESULT=I2P_ERROR MESSAGE=\"escaped \\\"quote\\\" and \\\\ slash\"");

        Assert.That(response.Values["MESSAGE"], Is.EqualTo("escaped \"quote\" and \\ slash"));
    }

    [Test]
    public void Parse_RejectsMalformedValueToken()
    {
        I2pException exception = Assert.Throws<I2pException>(() => I2pSamResponse.Parse("STREAM STATUS RESULT=OK BAD"))!;

        Assert.That(exception.Message, Does.Contain("BAD"));
    }

    [Test]
    public async Task ReadLineAsync_CapsCarriageReturnOnlyResponses()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            IPEndPoint endpoint = (IPEndPoint)listener.LocalEndpoint;
            Task serverTask = Task.Run(async () =>
            {
                using TcpClient serverClient = await listener.AcceptTcpClientAsync();
                await using NetworkStream serverStream = serverClient.GetStream();
                byte[] bytes = new byte[8193];
                Array.Fill(bytes, (byte)'\r');
                await serverStream.WriteAsync(bytes);
            });

            using TcpClient client = new();
            await client.ConnectAsync(endpoint.Address, endpoint.Port);
            await using NetworkStream stream = client.GetStream();

            MethodInfo readLine = typeof(I2pSamClient).GetMethod("ReadLineAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            Task<string> readTask = (Task<string>)readLine.Invoke(null, [stream, CancellationToken.None])!;

            I2pException exception = Assert.ThrowsAsync<I2pException>(async () => await readTask)!;

            Assert.That(exception.Message, Does.Contain("8192"));
            await serverTask;
        }
        finally
        {
            listener.Stop();
        }
    }
}
