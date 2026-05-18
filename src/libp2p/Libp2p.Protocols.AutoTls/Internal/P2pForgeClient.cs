// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Libp2p.Protocols.AutoTls.Internal;

/// <summary>
/// Client for the p2p-forge registration broker (registration.libp2p.direct).
/// Submits the ACME DNS-01 challenge value alongside the node's multiaddrs;
/// the broker verifies reachability + PeerID auth and publishes the TXT record.
/// </summary>
public sealed class P2pForgeClient
{
    private readonly HttpClient _http;
    private readonly AutoTlsOptions _options;
    private readonly ILogger<P2pForgeClient>? _logger;

    public P2pForgeClient(HttpClient http, IOptions<AutoTlsOptions> options, ILogger<P2pForgeClient>? logger = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SubmitChallengeAsync(
        Identity identity,
        string dns01Value,
        IEnumerable<Multiaddress> addresses,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(dns01Value);

        Uri endpoint = new(_options.ForgeRegistrationEndpoint);
        string hostname = endpoint.Host;

        // Step 1: probe to obtain the broker's challenge (server-issued challenge-client).
        using HttpRequestMessage probe = new(HttpMethod.Post, endpoint);
        using HttpResponseMessage probeResp = await _http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);

        string? wwwAuth = probeResp.Headers.WwwAuthenticate.FirstOrDefault()?.ToString();
        IReadOnlyDictionary<string, string> challenge = PeerIdAuth.ParseChallenge(wwwAuth);
        if (!challenge.TryGetValue("challenge-client", out string? serverChallenge) || string.IsNullOrEmpty(serverChallenge))
        {
            string body = await SafeReadAsync(probeResp, ct);
            throw new AutoTlsException($"Forge broker did not return a peer-id-auth challenge. Status={(int)probeResp.StatusCode}, Body={body}");
        }
        if (!challenge.TryGetValue("public-key", out string? serverPublicKey) || string.IsNullOrEmpty(serverPublicKey))
        {
            string body = await SafeReadAsync(probeResp, ct);
            throw new AutoTlsException($"Forge broker did not return a peer-id-auth public key. Status={(int)probeResp.StatusCode}, Body={body}");
        }
        challenge.TryGetValue("opaque", out string? opaque);

        // Step 2: send the signed payload + DNS-01 value + multiaddrs.
        string authHeader = PeerIdAuth.BuildAuthorizationHeader(identity, hostname, serverChallenge, serverPublicKey, opaque);
        ForgeChallengePayload payload = new()
        {
            Value = dns01Value,
            Addresses = addresses.Select(a => a.ToString()).ToArray(),
        };

        using HttpRequestMessage submit = new(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload),
        };
        submit.Headers.TryAddWithoutValidation("Authorization", authHeader);

        using HttpResponseMessage resp = await _http.SendAsync(submit, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string body = await SafeReadAsync(resp, ct);
            throw new AutoTlsException($"Forge broker rejected challenge submission. Status={(int)resp.StatusCode}, Body={body}");
        }

        _logger?.LogInformation("Forge broker accepted DNS-01 challenge for {PeerId}.", identity.PeerId);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class ForgeChallengePayload
    {
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("addresses")]
        public string[] Addresses { get; set; } = Array.Empty<string>();
    }
}
