// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Quic.Tests;

public class WindowsCertificateKeyTests
{
    private const string ProbePathVariable = "LIBP2P_WINDOWS_KEY_PROBE";

    [TestCase(false)]
    [TestCase(true)]
    public async Task CertificateDisposalPreservesTheProcessKeyAndOtherCertificates(bool concurrent)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("CNG key lifetime is Windows-specific.");
            return;
        }

        using ECDsa sessionKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 survivor = CertificateHelper.CertificateFromIdentity(sessionKey, new Identity());
        (string name, string publicKeyHash) expected = KeyInfo(survivor);
        Task<(string name, string publicKeyHash)>[] attempts = Enumerable.Range(0, 20).Select(_ => concurrent
            ? Task.Run(CreateAndDisposeCertificate)
            : Task.FromResult(CreateAndDisposeCertificate())).ToArray();
        var keys = await Task.WhenAll(attempts);
        Assert.That(keys, Is.All.EqualTo(expected), "All inputs, leases, and concurrent callers must reuse one named key.");
        Assert.That(CngKey.Exists(expected.name), Is.True, "A lease does not own the process key.");
        using ECDsa signer = survivor.GetECDsaPrivateKey()!;
        byte[] message = [1, 2, 3];
        byte[] signature = signer.SignData(message, HashAlgorithmName.SHA256);
        using ECDsa verifier = survivor.GetECDsaPublicKey()!;
        Assert.That(verifier.VerifyData(message, signature, HashAlgorithmName.SHA256), Is.True);
    }

    [Test]
    public async Task NormalProcessExitDeletesOnlyItsOwnKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("CNG key lifetime is Windows-specific.");
            return;
        }

        // The child runs this exact test in a fresh process, reports its exact key, then
        // exits normally. Waiting for that process tests the real ProcessExit cleanup.
        if (Environment.GetEnvironmentVariable(ProbePathVariable) is { } childReport)
        {
            var childKey = CreateAndDisposeCertificate();
            File.WriteAllLines(childReport, [childKey.name, childKey.publicKeyHash]);
            Assert.That(CngKey.Exists(childKey.name), Is.True, "The key must remain until process shutdown.");
            return;
        }

        var parentKey = CreateAndDisposeCertificate();
        string report = Path.Combine(Path.GetTempPath(), $"libp2p-key-probe-{Guid.NewGuid():N}.txt");
        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(WindowsCertificateKeyTests).Assembly.Location);
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add($"FullyQualifiedName={typeof(WindowsCertificateKeyTests).FullName}.{nameof(NormalProcessExitDeletesOnlyItsOwnKey)}");
        start.ArgumentList.Add("--minimum-expected-tests");
        start.ArgumentList.Add("1");
        start.Environment[ProbePathVariable] = report;
        using Process child = Process.Start(start) ?? throw new AssertionException("Could not start the key probe.");
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> errors = child.StandardError.ReadToEndAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        try
        {
            await child.WaitForExitAsync(deadline.Token);
            Assert.That(child.ExitCode, Is.Zero, $"{await output}\n{await errors}");
            string[] key = File.ReadAllLines(report);
            Assert.That(key, Has.Length.EqualTo(2));
            Assert.That(key[0], Is.Not.EqualTo(parentKey.name));
            Assert.That(CngKey.Exists(key[0]), Is.False, "Normal process shutdown must delete its owned key.");
            Assert.That(CngKey.Exists(parentKey.name), Is.True, "The parent remains active and owns a different key.");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            await Task.WhenAll(output, errors);
            if (File.Exists(report))
            {
                // Cleanup after a failed probe is limited to this exited child's exact
                // reported key, with its public-key fingerprint checked before deletion.
                string[] key = File.ReadAllLines(report);
                if (key.Length == 2 && key[0] != parentKey.name && CngKey.Exists(key[0]))
                {
                    using CngKey owned = CngKey.Open(key[0]);
                    using ECDsaCng signer = new(owned);
                    Assert.That(PublicKeyHash(signer), Is.EqualTo(key[1]), "Prove ownership before failed-probe cleanup.");
                    owned.Delete();
                }
                File.Delete(report);
            }
        }
    }

    private static (string name, string publicKeyHash) CreateAndDisposeCertificate()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using ECDsa input = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using CertificateHelper.CertificateLease lease = CertificateHelper.CreateCertificateLease(input, new Identity());
        return KeyInfo(lease.Certificate);
    }

    [SupportedOSPlatform("windows")]
    private static (string name, string publicKeyHash) KeyInfo(X509Certificate2 certificate)
    {
        using ECDsaCng signer = certificate.GetECDsaPrivateKey() as ECDsaCng
            ?? throw new AssertionException("Expected a CNG private key.");
        using CngKey key = signer.Key;
        Assert.That(key.IsEphemeral, Is.False, "Schannel requires a named key.");
        Assert.That(key.KeyName, Is.Not.Null.And.Not.Empty);
        return (key.KeyName!, PublicKeyHash(signer));
    }

    private static string PublicKeyHash(ECDsa key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
}
