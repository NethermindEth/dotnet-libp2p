// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Quic;
using NUnit.Framework;

namespace Libp2p.E2eTests;

[NonParallelizable]
public class RegisteredWindowsKeyRecoveryTests
{
    [Test]
    public async Task ChildListener()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Ignore("Windows CNG recovery."); return; }
        if (IsolatedTestProcess.Role is not { } transport) return;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        await using ServiceProvider services = RegisteredSecureTransportTests.Services(transport);
        await using ILocalPeer listener = services.GetRequiredService<IPeerFactory>().Create();
        Channel<ISession> accepted = System.Threading.Channels.Channel.CreateUnbounded<ISession>();
        listener.OnConnected += session => accepted.Writer.TryWrite(session);
        await listener.StartListenAsync([transport == "quic" ? "/ip4/127.0.0.1/udp/0/quic-v1" : "/ip4/127.0.0.1/tcp/0"], deadline.Token);
        IsolatedTestProcess.Publish("address", listener.ListenAddresses.Single().ToString());
        ISession inbound = await accepted.Reader.ReadAsync(deadline.Token);
        Assert.That(await inbound.DialAsync<IncrementNumberTestProtocol, int, int>(51, deadline.Token).WaitAsync(deadline.Token), Is.EqualTo(52));
        // This is the same key already used by the registered transport handshake.
        using ECDsa input = ECDsa.Create();
        using X509Certificate2 certificate = CertificateHelper.CertificateFromIdentity(input, listener.Identity);
        using ECDsaCng signer = (ECDsaCng)certificate.GetECDsaPrivateKey()!;
        using CngKey key = signer.Key;
        Assert.That(key.KeyName, Does.StartWith("libp2p-process-v1-"));
        IsolatedTestProcess.Publish("ready", key.KeyName!, Fingerprint(key), inbound.RemotePeerId.ToString());
        await IsolatedTestProcess.Receive("verify");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.That(await inbound.DialAsync<IncrementNumberTestProtocol, int, int>(61, deadline.Token).WaitAsync(deadline.Token), Is.EqualTo(62));
    }

    [Test]
    public async Task TlsCrashRestartPreservesLiveRegisteredTransport() => await CrashRestart("tls");

    [Test]
    public async Task QuicCrashRestartPreservesLiveRegisteredTransport() => await CrashRestart("quic");

    private static async Task CrashRestart(string transport)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Ignore("Windows CNG recovery."); return; }
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        await using ServiceProvider services = RegisteredSecureTransportTests.Services(transport);
        await using ILocalPeer dialer = services.GetRequiredService<IPeerFactory>().Create();
        await using IsolatedTestProcess live = Child(transport);
        (ISession liveSession, string[] liveKey) = await Connect(live);
        try
        {
            await using IsolatedTestProcess doomed = Child(transport);
            (_, string[] orphan) = await Connect(doomed);
            try
            {
                await doomed.Kill();
                Assert.That(CngKey.Exists(orphan[0]), Is.True, "The actual transport's key survives abnormal exit before recovery.");
                await using IsolatedTestProcess restarted = Child(transport);
                (ISession restartedSession, string[] replacement) = await Connect(restarted);
                try
                {
                    Assert.That(CngKey.Exists(orphan[0]), Is.False, "Fresh registered TLS/QUIC initialization recovers the orphan.");
                    Assert.That(replacement[0], Is.Not.EqualTo(orphan[0]));
                    Assert.That(replacement[1], Is.Not.EqualTo(orphan[1]));
                    Assert.That(CngKey.Exists(liveKey[0]), Is.True);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Assert.That(await liveSession.DialAsync<IncrementNumberTestProtocol, int, int>(71, deadline.Token).WaitAsync(deadline.Token), Is.EqualTo(72));
                    Assert.That(await restartedSession.DialAsync<IncrementNumberTestProtocol, int, int>(81, deadline.Token).WaitAsync(deadline.Token), Is.EqualTo(82));
                    // Both remote processes also open application streams back to us.
                    live.Send("verify");
                    restarted.Send("verify");
                    await Task.WhenAll(live.WaitForSuccess(), restarted.WaitForSuccess());
                }
                finally { await restarted.Kill(); Cleanup(replacement); }
            }
            finally { await doomed.Kill(); Cleanup(orphan); }
        }
        finally { await live.Kill(); Cleanup(liveKey); }

        async Task<(ISession session, string[] key)> Connect(IsolatedTestProcess child)
        {
            string[] address = await child.Read("address");
            Multiaddress destination = Multiaddress.Decode(address[0]);
            Multiaddress wrongPeer = destination.ToString().Replace($"/p2p/{destination.GetPeerId()}", $"/p2p/{TestPeers.PeerId(99)}");
            Exception? failure = Assert.CatchAsync(async () => await dialer.DialAsync(wrongPeer, deadline.Token).WaitAsync(deadline.Token));
            Assert.That(failure, Is.TypeOf<TaskCanceledException>().Or.TypeOf<PeerConnectionException>());
            Assert.That(deadline.IsCancellationRequested, Is.False);
            ISession session = await dialer.DialAsync(destination, deadline.Token).WaitAsync(deadline.Token);
            Assert.That(session.RemotePeerId, Is.EqualTo(destination.GetPeerId()));
            Assert.That(await session.DialAsync<IncrementNumberTestProtocol, int, int>(41, deadline.Token).WaitAsync(deadline.Token), Is.EqualTo(42));
            string[] key = await child.Read("ready");
            Assert.That(key, Has.Length.EqualTo(3));
            Assert.That(key[2], Is.EqualTo(dialer.Identity.PeerId.ToString()));
            return (session, key);
        }
    }

    private static IsolatedTestProcess Child(string transport) => new(typeof(RegisteredWindowsKeyRecoveryTests).Assembly,
        $"{typeof(RegisteredWindowsKeyRecoveryTests).FullName}.{nameof(ChildListener)}", transport);

    [SupportedOSPlatform("windows")]
    private static string Fingerprint(CngKey key)
    {
        using ECDsaCng signer = new(key);
        return Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo()));
    }

    [SupportedOSPlatform("windows")]
    private static void Cleanup(string[] report)
    {
        if (!CngKey.Exists(report[0])) return;
        using CngKey key = CngKey.Open(report[0]);
        Assert.That(Fingerprint(key), Is.EqualTo(report[1]), "Delete only this exited child's acknowledged key.");
        key.Delete();
    }
}
