// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;

namespace Nethermind.Libp2p.Generators.Enums;

[Generator]
public class EnumsGenerator : ISourceGenerator
{
    record MultiCodeCode(string Name, string Tag, string Code, string Status, string Desc);

    public void Execute(GeneratorExecutionContext context)
    {
        string? projectDirectory =
            context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.projectdir", out string? result)
                ? result
                : null;
        if (projectDirectory is null || !projectDirectory.Contains("src"))
        {
            return;
        }

        // This is not how generators should work, but
        // it allows to really add the enums to the project and make multicodec an **optional** git submodule
        string enumsDirectory = Path.Combine(projectDirectory, "Enums");
        if (!Directory.Exists(enumsDirectory))
        {
            return;
        }

        string filePath = projectDirectory.Substring(0, projectDirectory.IndexOf("src") + "src".Length) +
                          Path.DirectorySeparatorChar + "multicodec" + Path.DirectorySeparatorChar + "table.csv";
        if (!File.Exists(filePath))
        {
            return;
        }

        List<MultiCodeCode> vals = File.ReadAllLines(filePath)
            .Skip(1)
            .Select(l => l.Split(",").Select(x => x.Trim('\t', ' ')).ToArray())
            .Select(s => new MultiCodeCode(s[0], s[1], s[2], s[3], s[4]))
            .ToList();

        IEnumerable<IGrouping<string, MultiCodeCode>> grouped =
            vals.GroupBy(x => x.Tag);

        foreach (IGrouping<string, MultiCodeCode> g in grouped)
        {
            string? e = Cap(g.Key);
            IEnumerable<string> vs = g.Select(x =>
                    $"{(string.IsNullOrEmpty(x.Desc) ? "" : $"    // {x.Desc}\n")}" +
                    $"{(x.Status == "permanent" ? "" : $"    // {x.Status}\n")}" + $"    {Cap(x.Name)} = {x.Code},\n")
                .Concat(new[] { "    Unknown,\n" });
            File.WriteAllText(Path.Combine(enumsDirectory, $"{e}.cs"),
                $"namespace Nethermind.Libp2p.Core.Enums;\npublic enum {e}\n{{\n{string.Join("", vs)}}}\n");
        }

        string? Cap(string? s)
        {
            return string.IsNullOrEmpty(s)
                ? s
                : string.Join("", s!.Split("-").Select(x => char.ToUpper(x![0]) + x[1..]));
        }
    }

    public void Initialize(GeneratorInitializationContext context)
    {
    }
}
