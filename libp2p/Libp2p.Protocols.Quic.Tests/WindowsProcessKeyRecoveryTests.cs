// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using static Nethermind.Libp2p.Protocols.Quic.WindowsProcessKey;

namespace Nethermind.Libp2p.Protocols.Quic.Tests;

[NonParallelizable]
public class WindowsProcessKeyRecoveryTests
{
    [SetUp]
    public void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Requires the real Windows software KSP and process handles.");
    }

    [Test]
    public async Task ChildProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (IsolatedTestProcess.Role is not { } role) return;
        if (role == "certificate")
        {
            using ECDsa input = ECDsa.Create();
            using X509Certificate2 certificate = CertificateHelper.CertificateFromIdentity(input, new Identity());
            using ECDsaCng signer = (ECDsaCng)certificate.GetECDsaPrivateKey()!;
            using CngKey key = signer.Key;
            Ownership owner = ReadOwner(key);
            IsolatedTestProcess.Publish("ready", key.KeyName!, Fingerprint(key), Convert.ToHexString(owner.Encode()));
            await IsolatedTestProcess.Receive("continue");
            byte[] data = [1, 2, 3];
            using ECDsa verifier = certificate.GetECDsaPublicKey()!;
            Assert.That(verifier.VerifyData(data, signer.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256), Is.True);
            return;
        }
        if (role == "recover" || role == "hold-recovery")
        {
            bool recovered = Recover(() =>
            {
                if (role != "hold-recovery") return;
                IsolatedTestProcess.Publish("locked");
                IsolatedTestProcess.Receive("release").GetAwaiter().GetResult();
            });
            IsolatedTestProcess.Publish("ready", recovered.ToString());
            return;
        }
        if (role.StartsWith("native-", StringComparison.Ordinal))
        {
            Ownership owner = CurrentOwner();
            Check(Native.NCryptOpenStorageProvider(out SafeNCryptProviderHandle provider, CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider, 0));
            using (provider)
            {
                Check(NCryptCreatePersistedKey(provider, out SafeNCryptKeyHandle key, "ECDSA_P256", owner.KeyName, 0, 0));
                using (key)
                {
                    if (role != "native-before-property")
                    {
                        byte[] marker = owner.Encode();
                        Check(NCryptSetProperty(key, PropertyName, marker, marker.Length, unchecked((int)PropertyOptions)));
                    }
                    if (role == "native-after-finalize") Check(NCryptFinalizeKey(key, 0));
                    IsolatedTestProcess.Publish("ready", owner.KeyName, Convert.ToHexString(owner.Encode()));
                    IsolatedTestProcess.Receive("continue").GetAwaiter().GetResult();
                }
            }
            return;
        }
        Assert.Fail($"Unexpected child role {role}");
    }

    [Test]
    public async Task CrashRestartRecoversOnlyTheDeadIncarnation()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using IsolatedTestProcess live = Child("certificate");
        string[] liveKey = await live.Read("ready");
        try
        {
            for (int run = 0; run < 3; run++)
            {
                await using IsolatedTestProcess crashed = Child("certificate");
                string[] orphan = await crashed.Read("ready");
                try
                {
                    Ownership owner = ParseReport(orphan);
                    Assert.That(QueryOwner(owner), Is.EqualTo(OwnerState.Live));
                    await crashed.Kill();
                    Assert.That(QueryOwner(owner), Is.EqualTo(OwnerState.Dead));
                    Assert.That(EnumerateKeyNames(), Does.Contain(orphan[0]), "Abnormal termination bypasses ProcessExit cleanup.");
                    using (CngKey persisted = CngKey.Open(orphan[0])) Assert.That(ReadOwner(persisted), Is.EqualTo(owner));
                    await using IsolatedTestProcess restarted = Child("certificate");
                    string[] replacement = await restarted.Read("ready");
                    try
                    {
                        Assert.That(replacement[0], Is.Not.EqualTo(orphan[0]));
                        Assert.That(replacement[1], Is.Not.EqualTo(orphan[1]), "A restart never adopts another process's private key.");
                        Assert.That(EnumerateKeyNames(), Does.Not.Contain(orphan[0]));
                        Assert.That(EnumerateKeyNames(), Does.Contain(liveKey[0]));
                        restarted.Send("continue");
                        await restarted.WaitForSuccess();
                        Assert.That(EnumerateKeyNames(), Does.Not.Contain(replacement[0]));
                    }
                    finally { await restarted.Kill(); Cleanup(replacement); }
                }
                finally { await crashed.Kill(); Cleanup(orphan); }
            }
            live.Send("continue");
            await live.WaitForSuccess();
            Assert.That(EnumerateKeyNames(), Does.Not.Contain(liveKey[0]));
        }
        finally { await live.Kill(); Cleanup(liveKey); }
    }

    [Test]
    public async Task CreationBeforeProperty()
        => await CreationInterruption("native-before-property", finalized: false);
    [Test]
    public async Task CreationAfterProperty()
        => await CreationInterruption("native-after-property", finalized: false);
    [Test]
    public async Task CreationAfterFinalization()
        => await CreationInterruption("native-after-finalize", finalized: true);

    private static async Task CreationInterruption(string role, bool finalized)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using IsolatedTestProcess child = Child(role);
        string[] report = await child.Read("ready");
        Ownership owner = ParseNativeReport(report);
        try
        {
            // Creation is outside the recovery mutex. Inspect any visible entry while
            // its creator is paused, then kill at the acknowledged native stage.
            CheckEverySurvivor(report[0], owner);
            Assert.That(Recover(), Is.True);
            if (finalized) Assert.That(EnumerateKeyNames(), Does.Contain(report[0]), "A live finalized creator is retained.");
            await child.Kill();
            bool survives = CheckEverySurvivor(report[0], owner);
            Assert.That(survives, Is.EqualTo(finalized));
            Assert.That(Recover(), Is.True);
            Assert.That(EnumerateKeyNames(), Does.Not.Contain(report[0]));
        }
        finally
        {
            await child.Kill();
            // Acknowledged random name + creation identity authorizes exact cleanup,
            // even if an atomicity regression makes the survivor unusable/unmarked.
            if (EnumerateKeyNames().Contains(report[0]))
            {
                using CngKey key = CngKey.Open(report[0]);
                key.Delete();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool CheckEverySurvivor(string name, Ownership owner)
    {
        if (!EnumerateKeyNames().Contains(name)) return false;
        // An enumerated survivor that cannot be opened fails the test; it is never absence.
        using CngKey key = CngKey.Open(name);
        Assert.That(ReadOwner(key), Is.EqualTo(owner));
        return true;
    }

    [Test]
    public void ManagedCreationPersistsMarkerAfterReopen()
    {
        if (!OperatingSystem.IsWindows()) return;
        Ownership owner = CurrentOwner();
        using (CngKey created = CngKey.Create(CngAlgorithm.ECDsaP256, owner.KeyName, CreationParameters(owner)))
            Assert.That(ReadOwner(created), Is.EqualTo(owner));
        using CngKey reopened = CngKey.Open(owner.KeyName);
        try { Assert.That(ReadOwner(reopened), Is.EqualTo(owner)); }
        finally { reopened.Delete(); }
    }

    [Test]
    public void FailedManagedPropertyInitializationLeavesNoSurvivor()
    {
        if (!OperatingSystem.IsWindows()) return;
        Ownership owner = CurrentOwner();
        CngKeyCreationParameters parameters = CreationParameters(owner);
        parameters.Parameters.Add(new CngProperty("Length", [1], CngPropertyOptions.None));
        try
        {
            Assert.Throws<CryptographicException>(() =>
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                using CngKey unexpected = CngKey.Create(CngAlgorithm.ECDsaP256, owner.KeyName, parameters);
                unexpected.Delete();
            });
            Assert.That(EnumerateKeyNames(), Does.Not.Contain(owner.KeyName));
        }
        finally
        {
            if (EnumerateKeyNames().Contains(owner.KeyName))
            {
                using CngKey key = CngKey.Open(owner.KeyName);
                key.Delete();
            }
        }
    }

    [Test]
    public async Task RecoverySkipsContentionAndRevalidatesAfterAbandonment()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using IsolatedTestProcess live = Child("certificate");
        string[] liveKey = await live.Read("ready");
        await using IsolatedTestProcess crashed = Child("certificate");
        string[] orphan = await crashed.Read("ready");
        try
        {
            await crashed.Kill();
            await using IsolatedTestProcess holder = Child("hold-recovery");
            await holder.Read("locked");
            await using IsolatedTestProcess contender = Child("recover");
            Assert.That(await contender.Read("ready"), Is.EqualTo(new[] { "False" }), "The other process reached recovery and skipped the held mutex.");
            await contender.WaitForSuccess();
            Assert.That(EnumerateKeyNames(), Does.Contain(orphan[0]));
            // A creator must still finish initialization while recovery is blocked.
            await using IsolatedTestProcess creator = Child("certificate");
            string[] created = await creator.Read("ready");
            try
            {
                using Mutex retained = OpenRecoveryMutex(); // Preserve the abandoned kernel object.
                await holder.Kill();
                await using IsolatedTestProcess successor = Child("recover");
                Assert.That(await successor.Read("ready"), Is.EqualTo(new[] { "True" }));
                await successor.WaitForSuccess();
                Assert.That(EnumerateKeyNames(), Does.Not.Contain(orphan[0]));
                Assert.That(EnumerateKeyNames(), Does.Contain(liveKey[0]));
                Assert.That(EnumerateKeyNames(), Does.Contain(created[0]));
                creator.Send("continue");
                await creator.WaitForSuccess();
            }
            finally { await creator.Kill(); Cleanup(created); }
            live.Send("continue");
            await live.WaitForSuccess();
        }
        finally
        {
            await crashed.Kill(); Cleanup(orphan);
            await live.Kill(); Cleanup(liveKey);
        }
    }

    [Test]
    public async Task ConcurrentInitializationsKeepEveryLiveKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        IsolatedTestProcess[] children = Enumerable.Range(0, 3).Select(_ => Child("certificate")).ToArray();
        string[][] reports = [];
        try
        {
            reports = await Task.WhenAll(children.Select(child => child.Read("ready")));
            Assert.That(reports.Select(report => report[0]).Distinct().Count(), Is.EqualTo(3));
            Assert.That(reports.Select(report => report[1]).Distinct().Count(), Is.EqualTo(3));
            Assert.That(Recover(), Is.True);
            foreach (string[] report in reports)
            {
                Assert.That(QueryOwner(ParseReport(report)), Is.EqualTo(OwnerState.Live));
                Assert.That(EnumerateKeyNames(), Does.Contain(report[0]));
            }
            foreach (IsolatedTestProcess child in children) child.Send("continue");
            await Task.WhenAll(children.Select(child => child.WaitForSuccess()));
        }
        finally
        {
            foreach (IsolatedTestProcess child in children) await child.DisposeAsync();
            foreach (string[] report in reports) Cleanup(report);
        }
    }

    [Test]
    public async Task UnavailableSynchronizationStillCreatesMarkedKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        using EventWaitHandle incompatible = new(false, EventResetMode.ManualReset, RecoveryMutexName,
            new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
        Assert.That(Recover(), Is.False);
        await using IsolatedTestProcess creator = Child("certificate");
        string[] report = await creator.Read("ready");
        try
        {
            using (CngKey key = CngKey.Open(report[0])) Assert.That(ReadOwner(key), Is.EqualTo(ParseReport(report)));
            creator.Send("continue");
            await creator.WaitForSuccess();
        }
        finally { await creator.Kill(); Cleanup(report); }
    }

    [TestCase("legacy")]
    [TestCase("independent")]
    [TestCase("missing")]
    [TestCase("malformed")]
    [TestCase("future")]
    [TestCase("mismatch")]
    [TestCase("algorithm")]
    public void RecoveryRetainsKeysWithoutExactOwnership(string kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        Ownership owner = CurrentOwner();
        string name = kind switch
        {
            "legacy" => $"libp2p-{Guid.NewGuid():N}",
            "independent" => $"independent-{Guid.NewGuid():N}",
            _ => owner.KeyName,
        };
        CngKeyCreationParameters parameters = CreationParameters(owner);
        if (kind == "missing") parameters.Parameters.Clear();
        if (kind is "malformed" or "future" or "mismatch")
        {
            byte[] value = kind == "mismatch" ? (owner with { Nonce = Guid.NewGuid() }).Encode() : owner.Encode();
            if (kind == "malformed") value = value[..^1];
            if (kind == "future") value[8] = 2;
            parameters.Parameters.Clear();
            parameters.Parameters.Add(new CngProperty(PropertyName, value, PropertyOptions));
        }
        using CngKey key = CngKey.Create(kind == "algorithm" ? CngAlgorithm.ECDsaP384 : CngAlgorithm.ECDsaP256, name, parameters);
        try
        {
            bool queried = false;
            Assert.That(RecoverKey(key, _ => { queried = true; return OwnerState.Dead; }), Is.False);
            Assert.That(queried, Is.False, "Ownership must be verified before evaluating liveness.");
            Assert.That(Recover(), Is.True);
            Assert.That(EnumerateKeyNames(), Does.Contain(name));
        }
        finally { key.Delete(); }
    }

    [Test]
    public void UnknownLivenessAndFailedDeletionRetainEvidenceForRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        Ownership owner = CurrentOwner();
        using CngKey key = CngKey.Create(CngAlgorithm.ECDsaP256, owner.KeyName, CreationParameters(owner));
        try
        {
            Assert.That(RecoverKey(key, _ => OwnerState.Unknown), Is.False);
            Assert.That(RecoverKey(key, _ => OwnerState.Live), Is.False);
            Assert.Throws<CryptographicException>(() =>
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                RecoverKey(key, _ => OwnerState.Dead, _ => throw new CryptographicException("Injected deletion failure"));
            });
            Assert.That(ReadOwner(key), Is.EqualTo(owner));
            Assert.That(EnumerateKeyNames(), Does.Contain(owner.KeyName));
            CngKey? verified = null;
            Assert.That(RecoverKey(key, _ => OwnerState.Dead, same => { verified = same; same.Delete(); }), Is.True);
            Assert.That(verified, Is.SameAs(key));
            Assert.That(EnumerateKeyNames(), Does.Not.Contain(owner.KeyName));
        }
        finally { if (CngKey.Exists(owner.KeyName)) key.Delete(); }
    }

    private static IsolatedTestProcess Child(string role) => new(typeof(WindowsProcessKeyRecoveryTests).Assembly,
        $"{typeof(WindowsProcessKeyRecoveryTests).FullName}.{nameof(ChildProcess)}", role);

    [SupportedOSPlatform("windows")]
    private static Ownership ReadOwner(CngKey key)
    {
        Assert.That(TryParse(key.KeyName!, key.GetProperty(PropertyName, CngPropertyOptions.CustomProperty).GetValue(), out Ownership owner), Is.True);
        return owner;
    }

    private static Ownership ParseReport(string[] report)
    {
        Assert.That(report, Has.Length.EqualTo(3));
        Assert.That(TryParse(report[0], Convert.FromHexString(report[2]), out Ownership owner), Is.True);
        return owner;
    }

    private static Ownership ParseNativeReport(string[] report)
    {
        Assert.That(report, Has.Length.EqualTo(2));
        Assert.That(TryParse(report[0], Convert.FromHexString(report[1]), out Ownership owner), Is.True);
        return owner;
    }

    [SupportedOSPlatform("windows")]
    private static string Fingerprint(CngKey key)
    {
        using ECDsaCng signer = new(key);
        return Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
    }

    [SupportedOSPlatform("windows")]
    private static void Cleanup(string[] report)
    {
        if (!EnumerateKeyNames().Contains(report[0])) return;
        using CngKey key = CngKey.Open(report[0]);
        Assert.That(Fingerprint(key), Is.EqualTo(report[1]), "Cleanup only an acknowledged child's exact key after its exit.");
        Assert.That(ReadOwner(key), Is.EqualTo(ParseReport(report)));
        key.Delete();
    }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptCreatePersistedKey(SafeNCryptProviderHandle provider, out SafeNCryptKeyHandle key, string algorithm, string name, int legacy, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptSetProperty(SafeNCryptKeyHandle key, string property, byte[] value, int length, int flags);
    [DllImport("ncrypt.dll")]
    private static extern int NCryptFinalizeKey(SafeNCryptKeyHandle key, int flags);
}
