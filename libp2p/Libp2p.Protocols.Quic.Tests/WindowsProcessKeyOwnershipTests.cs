// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Libp2p.Core.TestsBase;
using static Nethermind.Libp2p.Protocols.Quic.WindowsProcessKey;

namespace Nethermind.Libp2p.Protocols.Quic.Tests;

public class WindowsProcessKeyOwnershipTests
{
    [Test]
    public async Task IsolatedProcessAcknowledgesAndExits()
    {
        if (IsolatedTestProcess.Role == "harness")
        {
            IsolatedTestProcess.Publish("ready", "acknowledged");
            Assert.That(await IsolatedTestProcess.Receive("finish"), Is.EqualTo(new[] { "finish" }));
            return;
        }
        await using IsolatedTestProcess child = new(typeof(WindowsProcessKeyOwnershipTests).Assembly,
            $"{typeof(WindowsProcessKeyOwnershipTests).FullName}.{nameof(IsolatedProcessAcknowledgesAndExits)}", "harness");
        Assert.That(await child.Read("ready"), Is.EqualTo(new[] { "acknowledged" }));
        child.Send("finish", "finish");
        await child.WaitForSuccess();
        Assert.That(child.HasTerminated, Is.True);
    }

    [Test]
    public async Task IsolatedKillWaitsForTheAcknowledgedProcessToTerminate()
    {
        await using IsolatedTestProcess child = new(typeof(WindowsProcessKeyOwnershipTests).Assembly,
            $"{typeof(WindowsProcessKeyOwnershipTests).FullName}.{nameof(IsolatedProcessAcknowledgesAndExits)}", "harness");
        Assert.That(await child.Read("ready"), Is.EqualTo(new[] { "acknowledged" }));
        using Process worker = Process.GetProcessById(child.ProcessId);
        _ = worker.SafeHandle; // Retain this incarnation before killing the directly launched worker.
        if (OperatingSystem.IsWindows()) Assert.That(worker.StartTime.ToFileTimeUtc(), Is.EqualTo(child.CreationTime));
        await child.Kill();
        Assert.That(worker.WaitForExit(0), Is.True, "The independently retained worker handle must be signaled when Kill returns.");
    }

    [Test]
    public void OwnershipRoundTripsExactly()
    {
        Ownership expected = new(123, 456, Guid.NewGuid());
        Assert.That(TryParse(expected.KeyName, expected.Encode(), out Ownership actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase("truncated")]
    [TestCase("extended")]
    [TestCase("magic")]
    [TestCase("future")]
    [TestCase("pid-zero")]
    [TestCase("pid-negative")]
    [TestCase("time-zero")]
    [TestCase("time-negative")]
    [TestCase("nonce-empty")]
    [TestCase("nonce-mismatch")]
    [TestCase("legacy")]
    [TestCase("case")]
    public void UnprovenOwnershipIsRejected(string defect)
    {
        Ownership owner = new(123, 456, Guid.NewGuid());
        byte[] value = owner.Encode();
        string name = owner.KeyName;
        switch (defect)
        {
            case "truncated": value = value[..^1]; break;
            case "extended": value = [.. value, 0]; break;
            case "magic": value[0] ^= 1; break;
            case "future": BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(8), 2); break;
            case "pid-zero": BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(12), 0); break;
            case "pid-negative": BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(12), -1); break;
            case "time-zero": BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(16), 0); break;
            case "time-negative": BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(16), -1); break;
            case "nonce-empty": value.AsSpan(24).Clear(); break;
            case "nonce-mismatch": name = $"{NamePrefix}{Guid.NewGuid():N}"; break;
            case "legacy": name = $"libp2p-{owner.Nonce:N}"; break;
            case "case": name = name.ToUpperInvariant(); break;
        }
        Assert.That(TryParse(name, value, out _), Is.False);
    }

    [TestCase(false, 87, false, 0, uint.MaxValue, OwnerState.Dead)]
    [TestCase(false, 5, false, 0, uint.MaxValue, OwnerState.Unknown)]
    [TestCase(false, 6, false, 0, uint.MaxValue, OwnerState.Unknown)]
    [TestCase(true, 0, false, 0, 0u, OwnerState.Unknown)]
    [TestCase(true, 0, true, 0, 0u, OwnerState.Unknown)]
    [TestCase(true, 0, true, 456, 258u, OwnerState.Live)]
    [TestCase(true, 0, true, 456, 0u, OwnerState.Dead)]
    [TestCase(true, 0, true, 457, 258u, OwnerState.Dead)]
    [TestCase(true, 0, true, 456, uint.MaxValue, OwnerState.Unknown)]
    [TestCase(true, 0, true, 456, 128u, OwnerState.Unknown)]
    public void LivenessRequiresProof(bool opened, int error, bool queried, long actual, uint wait, object expected)
        => Assert.That(ClassifyOwner(456, opened, error, queried, actual, wait), Is.EqualTo(expected));
}
