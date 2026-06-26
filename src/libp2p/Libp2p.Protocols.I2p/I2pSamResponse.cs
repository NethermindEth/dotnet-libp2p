// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Text;

namespace Nethermind.Libp2p.Protocols.I2p;

public sealed record I2pSamResponse(string Topic, string Type, IReadOnlyDictionary<string, string> Values)
{
    public string? Result => Values.GetValueOrDefault("RESULT");
    public bool IsOk => string.Equals(Result, "OK", StringComparison.OrdinalIgnoreCase);

    public static I2pSamResponse Parse(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        List<string> parts = Tokenize(line);
        if (parts.Count < 2)
        {
            throw new I2pException($"Invalid SAM response: {line}");
        }

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 2; i < parts.Count; i++)
        {
            int separator = parts[i].IndexOf('=');
            if (separator <= 0)
            {
                throw new I2pException($"Invalid SAM response token: {parts[i]}");
            }

            values[parts[i][..separator]] = parts[i][(separator + 1)..];
        }

        return new I2pSamResponse(parts[0], parts[1], values);
    }

    private static List<string> Tokenize(string line)
    {
        List<string> tokens = [];
        int index = 0;
        while (index < line.Length)
        {
            while (index < line.Length && line[index] == ' ')
            {
                index++;
            }

            if (index >= line.Length)
            {
                break;
            }

            int start = index;
            while (index < line.Length && line[index] != ' ' && line[index] != '=')
            {
                index++;
            }

            if (index >= line.Length || line[index] == ' ')
            {
                tokens.Add(line[start..index]);
                continue;
            }

            string key = line[start..index];
            index++;
            string value;
            if (index < line.Length && line[index] == '"')
            {
                index++;
                StringBuilder quotedValue = new();
                while (index < line.Length)
                {
                    if (line[index] == '"')
                    {
                        break;
                    }
                    if (line[index] == '\\' && index + 1 < line.Length
                        && (line[index + 1] == '"' || line[index + 1] == '\\'))
                    {
                        quotedValue.Append(line[index + 1]);
                        index += 2;
                        continue;
                    }

                    quotedValue.Append(line[index]);
                    index++;
                }
                if (index >= line.Length)
                {
                    throw new I2pException($"Invalid SAM quoted value: {line}");
                }

                value = quotedValue.ToString();
                index++;
            }
            else
            {
                start = index;
                while (index < line.Length && line[index] != ' ')
                {
                    index++;
                }

                value = line[start..index];
            }

            tokens.Add($"{key}={value}");
        }

        return tokens;
    }

    public void ThrowIfNotOk(string command)
    {
        if (IsOk)
        {
            return;
        }

        string result = Result ?? "missing RESULT";
        string message = Values.GetValueOrDefault("MESSAGE") ?? result;
        throw new I2pException($"SAM command '{command}' failed: {message}");
    }
}
