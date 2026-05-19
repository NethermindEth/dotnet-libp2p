// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.AutoTls.Internal;

public sealed class FileCertificateStore
{
    private const string AccountKeyFile = "account.key";
    private const string PfxExtension = ".pfx";

    private readonly AutoTlsOptions _options;
    private readonly ILogger<FileCertificateStore>? _logger;

    public FileCertificateStore(IOptions<AutoTlsOptions> options, ILogger<FileCertificateStore>? logger = null)
    {
        _options = options.Value;
        _logger = logger;
    }

    private string CertPath(string peerId) => Path.Combine(_options.CertificateStorePath, peerId + PfxExtension);
    private string AccountKeyPath => Path.Combine(_options.CertificateStorePath, AccountKeyFile);

    public X509Certificate2? TryLoad(string peerId)
    {
        string path = CertPath(peerId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            X509Certificate2 cert = X509CertificateLoader.LoadPkcs12(
                bytes,
                password: null,
                keyStorageFlags: X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            return cert;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load stored certificate at {Path}; will re-issue.", path);
            return null;
        }
    }

    public void Save(string peerId, X509Certificate2 certificate)
    {
        EnsureDirectory();
        string path = CertPath(peerId);
        byte[] pfx = certificate.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(path, pfx);
        TrySetOwnerOnlyPermissions(path);
        _logger?.LogInformation("Saved certificate for {PeerId} (NotAfter={NotAfter:o})", peerId, certificate.NotAfter);
    }

    public string? TryLoadAccountKey()
    {
        if (!File.Exists(AccountKeyPath))
        {
            return null;
        }
        return File.ReadAllText(AccountKeyPath);
    }

    public void SaveAccountKey(string pemEncodedKey)
    {
        EnsureDirectory();
        File.WriteAllText(AccountKeyPath, pemEncodedKey);
        TrySetOwnerOnlyPermissions(AccountKeyPath);
    }

    private void EnsureDirectory()
    {
        if (!Directory.Exists(_options.CertificateStorePath))
        {
            Directory.CreateDirectory(_options.CertificateStorePath);
            TrySetOwnerOnlyPermissions(_options.CertificateStorePath, isDirectory: true);
        }
    }

    private static void TrySetOwnerOnlyPermissions(string path, bool isDirectory = false)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            UnixFileMode mode = isDirectory ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // best-effort; ignore on platforms or filesystems that reject it
        }
    }
}
