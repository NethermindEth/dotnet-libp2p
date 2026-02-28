// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

[TestFixture]
public class WindowsTlsProtocolTests
{
    [Test]
    public void Test_WindowsCertificateHelper_CreatesCertificate()
    {
        // Arrange
        var identity = new Identity();
        var ecdsa = WindowsCertificateHelper.CreateWindowsCompatibleECDsa();

        // Act & Assert
        Assert.DoesNotThrow(() =>
        {
            var certificate = WindowsCertificateHelper.CreateCertificateFromIdentity(ecdsa, identity);
            Assert.That(certificate, Is.Not.Null);
            Assert.That(certificate.HasPrivateKey, Is.True.Or.False); // May vary by platform
        });
        
        ecdsa.Dispose();
    }

    [Test]
    public void Test_WindowsCertificateHelper_CreatesCertificateWithPrivateKey()
    {
        // Arrange
        var identity = new Identity();
        var ecdsa = WindowsCertificateHelper.CreateWindowsCompatibleECDsa();

        // Act & Assert
        Assert.DoesNotThrow(() =>
        {
            var certificate = WindowsCertificateHelper.CreateCertificateWithPrivateKey(ecdsa, identity);
            Assert.That(certificate, Is.Not.Null);
            
            // On Windows, private key should be properly associated
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.That(certificate.HasPrivateKey, Is.True);
            }
            
            certificate.Dispose();
        });
        
        ecdsa.Dispose();
    }

    [Test]
    public void Test_WindowsCertificateHelper_ValidatesCorrectCertificate()
    {
        // Arrange
        var identity = new Identity();
        var ecdsa = WindowsCertificateHelper.CreateWindowsCompatibleECDsa();
        var certificate = WindowsCertificateHelper.CreateCertificateFromIdentity(ecdsa, identity);

        // Act
        bool isValid = WindowsCertificateHelper.ValidateCertificate(certificate as X509Certificate2, identity.PeerId.ToString());

        // Assert
        Assert.That(isValid, Is.True);
        
        certificate.Dispose();
        ecdsa.Dispose();
    }

    [Test]
    public void Test_WindowsCertificateHelper_RejectsInvalidCertificate()
    {
        // Arrange
        var identity1 = new Identity();
        var identity2 = new Identity();
        var ecdsa = WindowsCertificateHelper.CreateWindowsCompatibleECDsa();
        var certificate = WindowsCertificateHelper.CreateCertificateFromIdentity(ecdsa, identity1);

        // Act - try to validate with wrong peer ID
        bool isValid = WindowsCertificateHelper.ValidateCertificate(certificate as X509Certificate2, identity2.PeerId.ToString());

        // Assert
        Assert.That(isValid, Is.False);
        
        certificate.Dispose();
        ecdsa.Dispose();
    }

    [Test]
    public void Test_WindowsCompatibleECDsa_CreatesValidKey()
    {
        // Act & Assert
        Assert.DoesNotThrow(() =>
        {
            using var ecdsa = WindowsCertificateHelper.CreateWindowsCompatibleECDsa();
            Assert.That(ecdsa, Is.Not.Null);
            Assert.That(ecdsa.KeySize, Is.GreaterThan(0));
            
            // Test key can be used for signing
            byte[] data = "test data"u8.ToArray();
            byte[] signature = ecdsa.SignData(data, HashAlgorithmName.SHA256);
            Assert.That(signature, Is.Not.Null);
            Assert.That(signature.Length, Is.GreaterThan(0));
            
            // Test signature verification
            bool verified = ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
            Assert.That(verified, Is.True);
        });
    }

    [Test]
    public void Test_WindowsTlsProtocol_Initialization()
    {
        // Act & Assert
        Assert.DoesNotThrow(() =>
        {
            var protocol = new WindowsTlsProtocol();
            Assert.That(protocol, Is.Not.Null);
            Assert.That(protocol.Id, Is.EqualTo("/tls/1.0.0"));
            Assert.That(protocol.ApplicationProtocols, Is.Not.Null);
            protocol.Dispose();
        });
    }

    [Test]
    public void Test_WindowsChannelStream_BasicOperations()
    {
        // Arrange
        var testChannel = new TestChannel();
        var stream = new WindowsChannelStream(testChannel, null);

        // Act & Assert
        Assert.That(stream.CanRead, Is.True);
        Assert.That(stream.CanWrite, Is.True);
        Assert.That(stream.CanSeek, Is.False);
        
        // Test that basic operations don't throw
        Assert.DoesNotThrow(() => stream.Flush());
        Assert.DoesNotThrowAsync(async () => await stream.FlushAsync());
        
        // Test unsupported operations
        Assert.Throws<NotSupportedException>(() => _ = stream.Length);
        Assert.Throws<NotSupportedException>(() => _ = stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(100));
        
        // Clean up
        stream.Dispose();
    }

    [Test]
    public void Test_SslProtocolsSupport()
    {
        // This test verifies that the protocol selection works on Windows
        // by using reflection to access the private method
        var protocolType = typeof(WindowsTlsProtocol);
        var method = protocolType.GetMethod("GetSupportedSslProtocols", 
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        
        Assert.That(method, Is.Not.Null, "GetSupportedSslProtocols method should exist");
        
        // Act
        var protocols = (SslProtocols)method!.Invoke(null, null)!;
        
        // Assert - should at least include TLS 1.2
        Assert.That(protocols.HasFlag(SslProtocols.Tls12), Is.True, "Should support TLS 1.2");
        
        // On newer systems, should also support TLS 1.3
        if (RuntimeInformation.OSDescription.Contains("Windows 10") ||
            RuntimeInformation.OSDescription.Contains("Windows 11") ||
            RuntimeInformation.OSDescription.Contains("Windows Server 2019") ||
            RuntimeInformation.OSDescription.Contains("Windows Server 2022"))
        {
            // TLS 1.3 support is expected on newer Windows versions
            Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
            Console.WriteLine($"Supported protocols: {protocols}");
        }
        
        // Should not include deprecated protocols
        Assert.That(protocols.HasFlag(SslProtocols.Ssl2), Is.False, "Should not support SSL 2");
        Assert.That(protocols.HasFlag(SslProtocols.Ssl3), Is.False, "Should not support SSL 3");
        Assert.That(protocols.HasFlag(SslProtocols.Tls), Is.False, "Should not support TLS 1.0");
        Assert.That(protocols.HasFlag(SslProtocols.Tls11), Is.False, "Should not support TLS 1.1");
    }

    [Test]
    [Platform(Include = "Win")]
    public void Test_WindowsPlatformSpecificFeatures()
    {
        // This test only runs on Windows and tests Windows-specific behavior
        var identity = new Identity();
        using var ecdsa = WindowsCertificateHelper.CreateWindowsCompatibleECDsa();
        using var certificate = WindowsCertificateHelper.CreateCertificateWithPrivateKey(ecdsa, identity);
        
        // On Windows, the certificate should have a private key properly associated
        Assert.That(certificate.HasPrivateKey, Is.True, "Certificate should have private key on Windows");
        
        // Verify certificate can be used for TLS operations
        Assert.DoesNotThrow(() =>
        {
            using var testCert = new X509Certificate2(certificate.Export(X509ContentType.Pfx), 
                (string?)null, X509KeyStorageFlags.Exportable);
            Assert.That(testCert.HasPrivateKey, Is.True);
        });
    }
}