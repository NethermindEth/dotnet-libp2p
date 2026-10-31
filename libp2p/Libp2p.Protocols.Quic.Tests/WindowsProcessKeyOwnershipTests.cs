// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security;
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

    [TestCase(false)]
    [TestCase(true)]
    public void OwnershipRoundTripsExactly(bool knownMachine)
    {
        Ownership expected = new(123, 456, Guid.NewGuid(), knownMachine ? Guid.NewGuid() : Guid.Empty);
        Assert.That(TryParse(expected.KeyName, expected.Encode(), out Ownership actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(OwnerState.Live)]
    [TestCase(OwnerState.Dead)]
    [TestCase(OwnerState.Unknown)]
    public void MatchingMachineUsesLocalLiveness(object state)
    {
        Ownership owner = new(123, 456, Guid.NewGuid(), Guid.NewGuid());
        bool queried = false;
        Assert.That(QueryOwner(owner, owner.MachineId, actual =>
        {
            Assert.That(actual, Is.EqualTo(owner));
            queried = true;
            return (OwnerState)state;
        }), Is.EqualTo(state));
        Assert.That(queried, Is.True);
    }

    [TestCase("foreign", false)]
    [TestCase("foreign", true)]
    [TestCase("missing-owner", false)]
    [TestCase("missing-owner", true)]
    [TestCase("missing-local", false)]
    [TestCase("missing-local", true)]
    [TestCase("missing-both", false)]
    [TestCase("missing-both", true)]
    public void OtherOrUnknownMachineNeverQueriesALocalProcess(string scope, bool reusedPid)
    {
        Ownership owner = new(123, 456, Guid.NewGuid(), Guid.NewGuid());
        Guid local = scope == "foreign" ? Guid.NewGuid() : owner.MachineId;
        if (scope is "missing-owner" or "missing-both") owner = owner with { MachineId = Guid.Empty };
        if (scope is "missing-local" or "missing-both") local = Guid.Empty;
        OwnerState localResult = ClassifyOwner(owner.CreationTime, reusedPid, 87, reusedPid, 457, 258);
        Assert.That(localResult, Is.EqualTo(OwnerState.Dead), "A missing or reused PID proves death only on the owning machine.");
        bool queried = false;
        Assert.That(QueryOwner(owner, local, _ => { queried = true; return localResult; }), Is.EqualTo(OwnerState.Unknown));
        Assert.That(queried, Is.False, "Do not query a local PID without matching machine evidence.");
    }

    [Test]
    public void MachineIdentityReadsTheExactRegistryGuid()
    {
        Guid machine = Guid.NewGuid();
        Assert.That(ReadMachineId(() => machine.ToString("D")), Is.EqualTo(machine));
    }

    [TestCase("missing")]
    [TestCase("malformed")]
    [TestCase("wrong-type")]
    [TestCase("empty")]
    [TestCase("denied")]
    [TestCase("security")]
    [TestCase("io")]
    public void UnavailableMachineIdentityRetainsKeys(string failure)
    {
        Guid machine = ReadMachineId(() => failure switch
        {
            "missing" => null,
            "malformed" => "invalid",
            "wrong-type" => 123,
            "empty" => Guid.Empty.ToString("D"),
            "denied" => throw new UnauthorizedAccessException(),
            "security" => throw new SecurityException(),
            _ => throw new IOException(),
        });
        Assert.That(machine, Is.EqualTo(Guid.Empty));
        Ownership owner = new(123, 456, Guid.NewGuid(), Guid.NewGuid());
        Assert.That(QueryOwner(owner, machine, _ => throw new AssertionException("A local process must not be queried.")),
            Is.EqualTo(OwnerState.Unknown));
    }

    [Test]
    public void RecoveryCandidatesReleaseEnumerationBeforeRecovery()
    {
        string first = $"{NamePrefix}{Guid.NewGuid():N}";
        string second = $"{NamePrefix}{Guid.NewGuid():N}";
        bool disposed = false;
        int enumerated = 0;
        IEnumerable<string> Names()
        {
            try
            {
                foreach (string name in new[] { "unrelated", first, first.ToUpperInvariant(), second })
                {
                    enumerated++;
                    yield return name;
                }
            }
            finally { disposed = true; }
        }

        List<string> recovered = [];
        foreach (string name in GetRecoveryCandidates(Names()))
        {
            Assert.That(disposed, Is.True, "The provider enumeration must be released before a candidate is opened or deleted.");
            Assert.That(enumerated, Is.EqualTo(4));
            recovered.Add(name);
        }
        Assert.That(recovered, Is.EqualTo(new[] { first, second }));
    }

    [Test]
    public void FailedCandidateEnumerationReleasesStateWithoutRecovery()
    {
        IOException failure = new("Injected enumeration failure.");
        bool disposed = false;
        IEnumerable<string> Names()
        {
            try
            {
                yield return $"{NamePrefix}{Guid.NewGuid():N}";
                throw failure;
            }
            finally { disposed = true; }
        }

        int recovered = 0;
        Assert.That(Assert.Throws<IOException>(() =>
        {
            foreach (string name in GetRecoveryCandidates(Names())) recovered++;
        }), Is.SameAs(failure));
        Assert.That(disposed, Is.True);
        Assert.That(recovered, Is.Zero, "Incomplete enumeration must not begin recovery.");
    }

    [TestCase("truncated")]
    [TestCase("extended")]
    [TestCase("magic")]
    [TestCase("future")]
    [TestCase("old-version")]
    [TestCase("v1")]
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
        Ownership owner = new(123, 456, Guid.NewGuid(), Guid.NewGuid());
        byte[] value = owner.Encode();
        string name = owner.KeyName;
        switch (defect)
        {
            case "truncated": value = value[..^1]; break;
            case "extended": value = [.. value, 0]; break;
            case "magic": value[0] ^= 1; break;
            case "future": BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(8), 3); break;
            case "old-version": BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(8), 1); break;
            case "v1":
                value = value[..40];
                BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(8), 1);
                name = $"libp2p-process-v1-{owner.Nonce:N}";
                break;
            case "pid-zero": BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(12), 0); break;
            case "pid-negative": BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(12), -1); break;
            case "time-zero": BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(16), 0); break;
            case "time-negative": BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(16), -1); break;
            case "nonce-empty": value.AsSpan(24, 16).Clear(); name = $"{NamePrefix}{Guid.Empty:N}"; break;
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
