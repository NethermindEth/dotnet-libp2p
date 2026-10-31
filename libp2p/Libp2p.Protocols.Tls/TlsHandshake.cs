// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT
// cspell:words Psks

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.CompilerServices;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using BcCertificate = Org.BouncyCastle.Tls.Certificate;
using BcProtocol = Org.BouncyCastle.Tls.TlsProtocol;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.Tls.Tests")]

namespace Nethermind.Libp2p.Protocols.Tls;

/// <summary>Connection-owned credentials and authentication state, private until Finished verifies.</summary>
internal sealed class TlsHandshake : IDisposable
{
    // RFC 8446 section 4: Handshake.length is uint24. There is no additional
    // application certificate-size cap. In particular, do not inherit BC's 32 KiB default.
    internal const int MaxHandshakeMessageSize = 0xFFFFFF;
    private static readonly ProtocolName Libp2p = ProtocolName.AsUtf8Encoding("libp2p");
    private readonly BcTlsCrypto _crypto = new();
    private readonly BcCertificate _certificate;
    private readonly AsymmetricKeyParameter _key;
    private readonly Func<X509Certificate2, bool> _validate;
    private readonly bool _server;
    private bool _clientCertificateRequested;

    internal BcProtocol Engine { get; }
    internal X509Certificate2? RemoteCertificate { get; private set; }
    internal bool Complete { get; private set; }
    internal bool PeerClosed => Engine is ClientEngine client ? client.PeerClosed : ((ServerEngine)Engine).PeerClosed;
    internal SslApplicationProtocol NegotiatedProtocol { get; private set; }

    internal TlsHandshake(X509Certificate2 certificate, bool server, Func<X509Certificate2, bool> validate)
    {
        _validate = validate;
        _server = server;
        _certificate = new BcCertificate([], [new CertificateEntry(_crypto.CreateCertificate(certificate.RawData), null)]);
        // The Windows certificate can use a different, process-owned key from TlsProtocol._sessionKey.
        using ECDsa key = certificate.GetECDsaPrivateKey()
            ?? throw new CryptographicException("TLS certificate has no ECDSA private key.");
        byte[] exported = key.ExportPkcs8PrivateKey();
        try { _key = PrivateKeyFactory.CreateKey(exported); }
        finally { CryptographicOperations.ZeroMemory(exported); }
        Engine = server ? new ServerEngine() : new ClientEngine();
    }

    internal void Start()
    {
        if (_server) ((ServerEngine)Engine).Accept(new Server(this));
        else ((ClientEngine)Engine).Connect(new Client(this));
    }

    internal void SendCloseNotify()
    {
        // TLS 1.3 permits receiving after sending close_notify. The adapter stops
        // admitting writes; keep the read side alive until the peer's closure.
        if (Engine is ClientEngine client) client.SendCloseNotify();
        else ((ServerEngine)Engine).SendCloseNotify();
    }

    private void Validate(BcCertificate certificate)
    {
        // This is the parsed, ordered Certificate list, not a built trust chain.
        // The parser limit also rejects a second entry before parsing its DER.
        if (certificate is null || certificate.Length != 1)
            throw new TlsFatalAlert(AlertDescription.bad_certificate);
        X509Certificate2 remote = X509CertificateLoader.LoadCertificate(certificate.GetCertificateAt(0).GetEncoded());
        try
        {
            if (!_validate(remote)) throw new TlsFatalAlert(AlertDescription.bad_certificate);
        }
        catch
        {
            remote.Dispose();
            throw;
        }
        RemoteCertificate?.Dispose();
        RemoteCertificate = remote;
    }

    private TlsCredentials Credentials(TlsContext context) => new BcDefaultTlsCredentialedSigner(
        new TlsCryptoParameters(context), _crypto, _key, _certificate,
        SignatureAndHashAlgorithm.GetInstance(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));

    private void Finished(TlsContext context)
    {
        SecurityParameters parameters = context.SecurityParameters;
        if (RemoteCertificate is null || (!_server && !_clientCertificateRequested) || parameters.IsResumedSession)
            throw new TlsFatalAlert(AlertDescription.handshake_failure);
        if (parameters.ApplicationProtocol is { } protocol)
        {
            if (!Libp2p.Equals(protocol)) throw new TlsFatalAlert(AlertDescription.no_application_protocol);
            NegotiatedProtocol = new SslApplicationProtocol("libp2p");
        }
        // Bouncy Castle invokes this only after CertificateVerify and peer Finished
        // validation. The channel adapter also drains our Finished before upgrading.
        Complete = true;
    }

    public void Dispose()
    {
        try { Engine.Close(); }
        finally { RemoteCertificate?.Dispose(); }
    }

    internal static void CheckCertificateEncoding(MemoryStream input)
    {
        // The supported receive hooks expose one bounded plaintext handshake
        // message. Validate nested lengths before BC allocates its DER/list arrays.
        long start = input.Position;
        long end = input.Length;
        try
        {
            Require(1, end);
            int contextLength = input.ReadByte();
            if (contextLength != 0) throw new TlsFatalAlert(AlertDescription.illegal_parameter);
            Require(contextLength + 3, end);
            input.Position += contextLength;
            int listLength = TlsUtilities.ReadUint24(input);
            if (listLength != end - input.Position) throw new TlsFatalAlert(AlertDescription.decode_error);
            if (listLength == 0) throw new TlsFatalAlert(AlertDescription.bad_certificate);
            Require(3, end);
            int certificateLength = TlsUtilities.ReadUint24(input);
            if (certificateLength == 0) throw new TlsFatalAlert(AlertDescription.bad_certificate);
            Require(certificateLength + 2, end);
            input.Position += certificateLength;
            int extensionsLength = TlsUtilities.ReadUint16(input);
            Require(extensionsLength, end);
            long extensionsEnd = input.Position + extensionsLength;
            while (input.Position < extensionsEnd)
            {
                Require(4, extensionsEnd);
                _ = TlsUtilities.ReadUint16(input);
                int length = TlsUtilities.ReadUint16(input);
                Require(length, extensionsEnd);
                input.Position += length;
            }
            if (input.Position != end) throw new TlsFatalAlert(AlertDescription.bad_certificate);
        }
        finally { input.Position = start; }

        void Require(int length, long limit)
        {
            if (length > limit - input.Position) throw new TlsFatalAlert(AlertDescription.decode_error);
        }
    }

    private sealed class ClientEngine : TlsClientProtocol
    {
        private bool _closeSent;
        internal bool PeerClosed { get; private set; }
        protected override void HandleAlertWarningMessage(short description)
        {
            if (description == AlertDescription.close_notify)
            {
                if (!IsConnected) throw new TlsFatalAlert(AlertDescription.handshake_failure);
                // RFC 8446 section 6.1: this closes only the peer's write side.
                // Keep our write side available for replies until the application ends it.
                PeerClosed = true;
                return;
            }
            base.HandleAlertWarningMessage(description);
        }
        internal void SendCloseNotify() => RaiseAlertWarning(AlertDescription.close_notify, "Application write side ended");
        protected override void RaiseAlertWarning(short description, string message)
        {
            if (description == AlertDescription.close_notify)
            {
                if (_closeSent) return;
                _closeSent = true;
            }
            base.RaiseAlertWarning(description, message);
        }
        protected override void Receive13ServerCertificate(MemoryStream input)
        {
            CheckCertificateEncoding(input);
            base.Receive13ServerCertificate(input);
        }
    }

    private sealed class ServerEngine : TlsServerProtocol
    {
        private bool _closeSent;
        internal bool PeerClosed { get; private set; }
        protected override void HandleAlertWarningMessage(short description)
        {
            if (description == AlertDescription.close_notify)
            {
                if (!IsConnected) throw new TlsFatalAlert(AlertDescription.handshake_failure);
                // RFC 8446 section 6.1: this closes only the peer's write side.
                // Keep our write side available for replies until the application ends it.
                PeerClosed = true;
                return;
            }
            base.HandleAlertWarningMessage(description);
        }
        internal void SendCloseNotify() => RaiseAlertWarning(AlertDescription.close_notify, "Application write side ended");
        protected override void RaiseAlertWarning(short description, string message)
        {
            if (description == AlertDescription.close_notify)
            {
                if (_closeSent) return;
                _closeSent = true;
            }
            base.RaiseAlertWarning(description, message);
        }
        protected override void Receive13ClientCertificate(MemoryStream input)
        {
            CheckCertificateEncoding(input);
            base.Receive13ClientCertificate(input);
        }
    }

    private sealed class Client(TlsHandshake owner) : DefaultTlsClient(owner._crypto)
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv13];
        protected override IList<ProtocolName> GetProtocolNames() => [Libp2p];
        public override int GetMaxCertificateChainLength() => 1;
        public override int GetMaxHandshakeMessageSize() => MaxHandshakeMessageSize;
        public override TlsSession GetSessionToResume() => null!;
        public override IList<TlsPskExternal> GetExternalPsks() => null!;
        public override short[] GetPskKeyExchangeModes() => [];
        public override void NotifySelectedPsk(TlsPsk selectedPsk)
        {
            if (selectedPsk is not null) throw new TlsFatalAlert(AlertDescription.handshake_failure);
        }
        public override TlsAuthentication GetAuthentication() => new Authentication(owner, () => owner.Credentials(m_context));
        public override void NotifyHandshakeComplete() => owner.Finished(m_context);
    }

    private sealed class Server(TlsHandshake owner) : DefaultTlsServer(owner._crypto)
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv13];
        protected override IList<ProtocolName> GetProtocolNames() => [Libp2p];
        public override int GetMaxCertificateChainLength() => 1;
        public override int GetMaxHandshakeMessageSize() => MaxHandshakeMessageSize;
        public override TlsSession GetSessionToResume(byte[] sessionID) => null!;
        public override byte[] GetNewSessionID() => null!;
        public override TlsPskExternal GetExternalPsk(IList<PskIdentity> identities) => null!;
        public override NewSessionTicket GetNewSessionTicket() => null!;
        public override short[] GetPskKeyExchangeModes() => [];
        public override TlsCredentials GetCredentials() => owner.Credentials(m_context);
        public override Org.BouncyCastle.Tls.CertificateRequest GetCertificateRequest() =>
            new([], TlsUtilities.GetDefaultSupportedSignatureAlgorithms(m_context), null, null);
        public override void NotifyClientCertificate(BcCertificate clientCertificate) => owner.Validate(clientCertificate);
        public override void NotifyHandshakeComplete() => owner.Finished(m_context);
    }

    private sealed class Authentication(TlsHandshake owner, Func<TlsCredentials> credentials) : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate certificate) => owner.Validate(certificate.Certificate);
        public TlsCredentials GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest request)
        {
            owner._clientCertificateRequested = true;
            return credentials();
        }
    }
}
