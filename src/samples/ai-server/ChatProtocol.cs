// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using System.Text;
using System.Text.Json;

internal class ChatProtocol : SymmetricSessionProtocol, ISessionProtocol
{
    private readonly ConsoleColor defaultConsoleColor = Console.ForegroundColor;

    public string Id => "/chat/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, ISessionContext context, bool isListener)
    {
        if (isListener)
        {
            await channel.WriteLineAsync("Hello from AI!");
        }

        using var client = new HttpClient();

        for (; ; )
        {
            string msg = await channel.ReadLineAsync();

            var request = new
            {
                model = "qwen2:0.5b",
                prompt = msg
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("http://localhost:11434/api/generate", content);
            var stream = await response.Content.ReadAsStreamAsync();

            using var reader = new StreamReader(stream);

            var sb = new StringBuilder();

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
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
