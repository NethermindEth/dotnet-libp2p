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
            foreach (var file in context.AdditionalFiles)
            {
                Process cmd = new();
                cmd.StartInfo.RedirectStandardError = true;
                cmd.StartInfo.FileName = _protocLocation;
                cmd.StartInfo.UseShellExecute = false;
                cmd.StartInfo.CreateNoWindow = true;
                cmd.StartInfo.WorkingDirectory = Path.GetDirectoryName(file.Path);
                cmd.StartInfo.Arguments = $"-I=. --csharp_out=. \"{Path.GetFileName(file.Path)}\"";
                cmd.Start();
                cmd.WaitForExit();
                if (cmd.ExitCode != 0)
                {
                    string errorLogs = cmd.StandardError.ReadToEnd();
                    throw new ApplicationException(errorLogs);
                }
                var output = cmd.StandardOutput.ReadToEnd();
            }
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
