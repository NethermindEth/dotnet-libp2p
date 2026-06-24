// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using System.Text;
using System.Text.Json;

internal class ChatProtocol(CancellationToken shutdownToken = default) : SymmetricSessionProtocol, ISessionProtocol
{
    public string Id => "/chat/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, ISessionContext context, bool isListener)
    {
        CancellationToken token = shutdownToken;

        if (isListener)
        {
            await channel.WriteLineAsync("Hello from AI!");
        }

        using var client = new HttpClient();

        while (!token.IsCancellationRequested)
        {
            string msg = await channel.ReadLineAsync();

            var request = new
            {
                model = "qwen2:0.5b",
                prompt = msg
            };

            var json = JsonSerializer.Serialize(request);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await client.PostAsync("http://localhost:11434/api/generate", content, token);
            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(token);
                await channel.WriteLineAsync($"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}: {error}");
                continue;
            }

            using var stream = await response.Content.ReadAsStreamAsync(token);

            using var reader = new StreamReader(stream);

            var sb = new StringBuilder();

            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("response", out var resp))
                {
                    sb.Append(resp.GetString());
                }
            }

            await channel.WriteLineAsync(sb.ToString());
        }
    }
}
