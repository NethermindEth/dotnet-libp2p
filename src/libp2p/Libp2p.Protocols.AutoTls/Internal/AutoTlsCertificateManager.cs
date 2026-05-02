// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.AutoTls.Internal;

/// <summary>
/// Owns the AutoTLS lifecycle: load-or-issue on start, then renew before expiry.
/// Acts as the <see cref="ITlsCertificateProvider"/> consumed by transports that
/// need a browser-trusted certificate (e.g. Secure WebSocket).
/// </summary>
public sealed class AutoTlsCertificateManager : IHostedService, ITlsCertificateProvider, IDisposable
{
    private readonly AutoTlsOptions _options;
    private readonly FileCertificateStore _store;
    private readonly AcmeFlow _acme;
    private readonly ILogger<AutoTlsCertificateManager>? _logger;

    private readonly TaskCompletionSource<X509Certificate2> _firstIssued =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Identity? _identity;
    private IReadOnlyList<Multiaddress>? _addresses;

    public AutoTlsCertificateManager(
        IOptions<AutoTlsOptions> options,
        FileCertificateStore store,
        AcmeFlow acme,
        ILogger<AutoTlsCertificateManager>? logger = null)
    {
        _options = options.Value;
        _store = store;
        _acme = acme;
        _logger = logger;
    }

    public X509Certificate2? Current { get; private set; }
    public event Action<X509Certificate2>? CertificateChanged;

    public Task<X509Certificate2> WaitForCertificateAsync(CancellationToken ct)
    {
        if (Current is not null)
        {
            return Task.FromResult(Current);
        }
        return _firstIssued.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Provide the local peer identity and the addresses the broker should probe.
    /// Call once the libp2p host is listening and publicly reachable.
    /// Subsequent calls update the addresses without re-issuing unless renewal is due.
    /// </summary>
    public void Configure(Identity identity, IReadOnlyList<Multiaddress> announcedAddresses)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(announcedAddresses);

        _identity = identity;
        _addresses = announcedAddresses;

        // If the loop is already running and we just got addresses for the first time, it'll pick them up.
        // The loop wakes on its CTS when not started yet; nothing to signal here.
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger?.LogDebug(ex, "AutoTLS loop ended with error."); }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        TimeSpan retry = _options.RetryDelay;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_identity is null || _addresses is null)
                {
                    // Wait for Configure() to be called.
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                Identity identity = _identity;
                IReadOnlyList<Multiaddress> addresses = _addresses;
                string peerId = identity.PeerId.ToString();

                X509Certificate2? cached = _store.TryLoad(peerId);
                if (cached is not null && cached.NotAfter - DateTime.UtcNow > _options.RenewBefore)
                {
                    PublishCertificate(cached);
                    TimeSpan untilRenew = cached.NotAfter - DateTime.UtcNow - _options.RenewBefore;
                    _logger?.LogInformation("Loaded cached AutoTLS certificate; next renewal in {Delay}", untilRenew);
                    await Task.Delay(untilRenew, ct);
                    continue;
                }

                X509Certificate2 issued = await _acme.IssueAsync(identity, addresses, ct);
                PublishCertificate(issued);
                retry = _options.RetryDelay; // reset backoff on success

                TimeSpan delay = issued.NotAfter - DateTime.UtcNow - _options.RenewBefore;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.FromMinutes(1);
                }
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "AutoTLS issuance failed; retrying in {Retry}", retry);
                try { await Task.Delay(retry, ct); }
                catch (OperationCanceledException) { return; }
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, _options.MaxRetryDelay.Ticks));
            }
        }
    }

    private void PublishCertificate(X509Certificate2 cert)
    {
        _gate.Wait();
        try
        {
            Current = cert;
            if (!_firstIssued.Task.IsCompleted)
            {
                _firstIssued.TrySetResult(cert);
            }
        }
        finally
        {
            _gate.Release();
        }
        try { CertificateChanged?.Invoke(cert); }
        catch (Exception ex) { _logger?.LogWarning(ex, "CertificateChanged subscriber threw."); }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _gate.Dispose();
    }
}
