// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.AutoTls.Internal;
using Google.Protobuf;
using System.Net;
using System.Text.Json;

namespace Nethermind.Libp2p.Protocols.AutoTls.Tests;

public class P2pForgeClientTests
{
    [Test]
    public async Task SubmitChallengeSendsLowercaseBrokerPayloadAndPeerIdAuthHeader()
    {
        Identity server = new();
        string challengeClient = Base64UrlEncode(Enumerable.Repeat((byte)0x22, 32).ToArray());
        string serverPublicKey = Base64UrlEncode(server.PublicKey.ToByteArray());
        SequenceHandler handler = new(challengeClient, serverPublicKey);
        using HttpClient http = new(handler);
        P2pForgeClient client = new(http, Options.Create(new AutoTlsOptions()));

        await client.SubmitChallengeAsync(
            new Identity(),
            "j8zQJv8v0XCMszhZ62P5k1iq6gEfncVm7E36OdGOWnA",
            [Multiaddress.Decode("/ip4/203.0.113.10/tcp/4001")],
            CancellationToken.None);

        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[1].Authorization, Does.StartWith("libp2p-PeerID "));
        Assert.That(handler.Requests[1].Authorization, Does.Contain("challenge-server="));
        Assert.That(handler.Requests[1].Authorization, Does.Contain("public-key="));
        Assert.That(handler.Requests[1].Authorization, Does.Contain("sig="));
        Assert.That(handler.Requests[1].Authorization, Does.Contain("opaque="));

        using JsonDocument document = JsonDocument.Parse(handler.Requests[1].Body);
        JsonElement root = document.RootElement;
        Assert.That(root.TryGetProperty("value", out JsonElement value), Is.True);
        Assert.That(value.GetString(), Is.EqualTo("j8zQJv8v0XCMszhZ62P5k1iq6gEfncVm7E36OdGOWnA"));
        Assert.That(root.TryGetProperty("addresses", out JsonElement addresses), Is.True);
        Assert.That(addresses[0].GetString(), Is.EqualTo("/ip4/203.0.113.10/tcp/4001"));
        Assert.That(root.TryGetProperty("Value", out _), Is.False);
        Assert.That(root.TryGetProperty("Addresses", out _), Is.False);
    }

    [Test]
    public void AutoTlsDomainFormatsLibp2pDirectIpv4Host()
    {
        Identity identity = new();

        string host = AutoTlsDomain.GetIpHost(identity.PeerId, IPAddress.Parse("203.0.113.1"));

        Assert.That(host, Does.StartWith("203-0-113-1.k"));
        Assert.That(host, Does.EndWith(".libp2p.direct"));
        Assert.That(host, Does.Contain(AutoTlsDomain.GetPeerDomain(identity.PeerId)));
    }

    private sealed class SequenceHandler(string challengeClient, string serverPublicKey) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                body));

            if (Requests.Count == 1)
            {
                HttpResponseMessage challenge = new(HttpStatusCode.Unauthorized);
                challenge.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    $"libp2p-PeerID challenge-client=\"{challengeClient}\", public-key=\"{serverPublicKey}\", opaque=\"opaque-value\"");
                return challenge;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string Authorization, string Body);

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_');
}
