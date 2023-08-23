// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Nethermind.Libp2p.Generators.Protobuf;

[Generator]
public class ProtobufGenerator : ISourceGenerator
{
    private string? _protocLocation;

    public void Execute(GeneratorExecutionContext context)
    {
        try
        {
            context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.projectdir",
                out string? projectDirectory);
            if (projectDirectory is null)
            {
                return;
            }

            string dtoDirectory = Path.Combine(projectDirectory, "Dto");
            IEnumerable<string> files = Directory.GetFiles(dtoDirectory, "*.proto")
                .Select(fname => Path.GetRelativePath(dtoDirectory, fname));
            context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.RootNamespace",
                out string? rootNamespace);
            Process cmd = new();
            cmd.StartInfo.RedirectStandardError = true;
            cmd.StartInfo.FileName = _protocLocation;
            cmd.StartInfo.CreateNoWindow = true;
            cmd.StartInfo.WorkingDirectory = dtoDirectory;
            cmd.StartInfo.Arguments =
                "-I=. --csharp_out=. " + String.Join(" ", files);
            cmd.Start();
            cmd.WaitForExit();
            if (cmd.ExitCode == 0)
            {
                return;
            }

            string errorLogs = cmd.StandardError.ReadToEnd();
            context.AddSource("ErrorLog.txt", $"//An error appeared during protobuf generation\n//{errorLogs}");
        }
        catch (Exception e)
        {
            context.AddSource("ErrorLog.txt",
                $"//An error appeared during protobuf generation\n/*\n{e}\n{e.StackTrace}*/");
        }
    }

    public void Initialize(GeneratorInitializationContext context)
    {
        _protocLocation = Assembly.GetExecutingAssembly()?
            .GetCustomAttribute<ProtocLocationAttribute>()?
            .ProtocLocation;
    }
}
