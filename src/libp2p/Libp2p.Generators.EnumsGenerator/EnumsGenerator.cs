using Microsoft.CodeAnalysis;

namespace Libp2p.Generators.EnumsGenerator;

[Generator]
public class EnumsGenerator : ISourceGenerator
{
    public void Execute(GeneratorExecutionContext context)
    {
        string? projectDir =
            context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.projectdir", out string? result)
                ? result
                : null;
        if (projectDir is null || !projectDir.Contains("src"))
        {
            return;
        }

        string filePath = projectDir.Substring(0, projectDir.IndexOf("src") + "src".Length) +
                          Path.DirectorySeparatorChar + "multicodec" + Path.DirectorySeparatorChar + "table.csv";
        if (!File.Exists(filePath))
        {
            return;
        }

        List<(string name, string tag, string code, string status, string desc)> vals = new();
        foreach (string l in File.ReadAllLines(filePath)
                     .Skip(1))
        {
            string[] s = l.Split(",").Select(x => x.Trim('\t', ' ')).ToArray();
            vals.Add((s[0], s[1], s[2], s[3], s[4]));
        }

        IEnumerable<IGrouping<string, (string name, string tag, string code, string status, string desc)>> grouped =
            vals.GroupBy(x => x.tag);
        Console.WriteLine();

        foreach (IGrouping<string, (string name, string tag, string code, string status, string desc)> g in grouped)
        {
            string? e = Cap(g.Key);
            IEnumerable<string> vs = g.Select(x =>
                    $"{(Noe(x.desc) ? "" : $"    // {x.desc}\n")}" +
                    $"{(x.status == "permanent" ? "" : $"    // {x.status}\n")}" + $"    {Cap(x.name)} = {x.code},\n")
                .Concat(new[] { "    Unknown,\n" });
            context.AddSource($"{e}.cs", $"namespace Libp2p.Enums;\npublic enum {e}\n{{\n{string.Join("", vs)}}}\n");
        }


        string? Cap(string? s)
        {
            return Noe(s) ? s : string.Join("", s!.Split("-").Select(x => char.ToUpper(x![0]) + x.Substring(1)));
        }

        bool Noe(string? s)
        {
            return s is null || s.Trim().Length == 0;
        }
    }

    public void Initialize(GeneratorInitializationContext context)
    {
    }
}
