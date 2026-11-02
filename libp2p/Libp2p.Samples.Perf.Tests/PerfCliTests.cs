// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text.Json;
using DataTransferBenchmark;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NUnit.Framework;

namespace Nethermind.Libp2p.Samples.Perf.Tests;

[TestFixture]
public class PerfCliTests
{
    [Test]
    public void ParsesClientArguments()
    {
        PerfArgs args = PerfArgs.Parse([
            "--server-address", "127.0.0.1:4001",
            "--transport", "quic-v1",
            "--upload-bytes", "10",
            "--download-bytes", "20"]);

        Assert.Multiple(() =>
        {
            Assert.That(args.RunServer, Is.False);
            Assert.That(args.ServerAddress, Is.EqualTo("127.0.0.1:4001"));
            Assert.That(args.Transport, Is.EqualTo("quic-v1"));
            Assert.That(args.UploadBytes, Is.EqualTo(10UL));
            Assert.That(args.DownloadBytes, Is.EqualTo(20UL));
        });
    }

    [Test]
    public void ServerModeDoesNotRequireAddressAndDefaultsToTcp()
    {
        PerfArgs args = PerfArgs.Parse(["--run-server"]);

        Assert.Multiple(() =>
        {
            Assert.That(args.RunServer, Is.True);
            Assert.That(args.ServerAddress, Is.Null);
            Assert.That(args.Transport, Is.EqualTo("tcp"));
        });
    }

    [Test]
    public void ClientModeRequiresServerAddress()
    {
        Assert.Throws<ArgumentException>(() => PerfArgs.Parse(["--transport", "tcp"]));
    }

    [Test]
    public void RejectsUnknownArgument()
    {
        Assert.Throws<ArgumentException>(() => PerfArgs.Parse(["--bogus"]));
    }

    [Test]
    public void RejectsFlagWithoutValue()
    {
        Assert.Throws<ArgumentException>(() => PerfArgs.Parse(["--server-address"]));
    }

    [Test]
    public void RejectsNonNumericByteCount()
    {
        Assert.Throws<FormatException>(() =>
            PerfArgs.Parse(["--server-address", "127.0.0.1:4001", "--upload-bytes", "abc"]));
    }

    [Test]
    public void CliModeIsDetectedOnlyWhenFlagsArePresent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PerfArgs.IsCliMode([]), Is.False);
            Assert.That(PerfArgs.IsCliMode(["--run-server"]), Is.True);
        });
    }

    [Test]
    public void ResultSerializesWithTestPlansFieldNames()
    {
        string json = JsonSerializer.Serialize(new PerfResult("final", 1.5, 2, 3));

        Assert.That(json, Is.EqualTo("{\"type\":\"final\",\"timeSeconds\":1.5,\"uploadBytes\":2,\"downloadBytes\":3}"));
    }

    [Test]
    public async Task ReporterCountsProtocolBytesAndPrintsFinalLine()
    {
        TextWriter originalOut = Console.Out;
        StringWriter captured = new();
        Console.SetOut(captured);
        PerfReporter? reporter = null;
        try
        {
            reporter = new PerfReporter();
            PerfProtocol.Reporter = reporter;
            PerfProtocol.BytesToSend = 3;
            PerfProtocol.BytesToReceive = 2;
            TestChannel channel = new();
            IChannel remote = channel.Reverse();

            Task dial = new PerfProtocol().DialAsync(channel, null!);

            await remote.ReadAsync(8).OrThrow();
            await remote.ReadAsync(3).OrThrow();
            Assert.That((await remote.ReadAsync(0, ReadBlockingMode.WaitAny)).Result, Is.EqualTo(IOResult.Ended));
            await remote.WriteAsync(new ReadOnlySequence<byte>(new byte[2]));
            await remote.WriteEofAsync();
            await dial;

            reporter.Final();
        }
        finally
        {
            PerfProtocol.Reporter = null;
            reporter?.Dispose();
            Console.SetOut(originalOut);
        }

        string[] lines = captured.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        using JsonDocument last = JsonDocument.Parse(lines[^1]);
        JsonElement root = last.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("final"));
            Assert.That(root.GetProperty("uploadBytes").GetUInt64(), Is.EqualTo(3UL));
            Assert.That(root.GetProperty("downloadBytes").GetUInt64(), Is.EqualTo(2UL));
            Assert.That(root.GetProperty("timeSeconds").GetDouble(), Is.GreaterThanOrEqualTo(0));
        });
    }
}