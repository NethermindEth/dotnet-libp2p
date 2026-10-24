// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.TestsBase;

// File acknowledgements keep child barriers independent of test-runner console output.
// Every process and temporary directory has one explicit test owner.
public sealed class IsolatedTestProcess : IAsyncDisposable
{
    private const string DirectoryVariable = "LIBP2P_ISOLATED_TEST_DIRECTORY";
    private const string RoleVariable = "LIBP2P_ISOLATED_TEST_ROLE";
    private readonly string _directory;
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _errors;

    public static string? Role => Environment.GetEnvironmentVariable(RoleVariable);

    public IsolatedTestProcess(Assembly assembly, string fullTestName, string role)
    {
        _directory = Path.Combine(Path.GetTempPath(), $"libp2p-child-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { assembly.Location, "--filter", $"FullyQualifiedName={fullTestName}", "--minimum-expected-tests", "1" })
            start.ArgumentList.Add(argument);
        start.Environment[DirectoryVariable] = _directory;
        start.Environment[RoleVariable] = role;
        // Avoid child reports overwriting the parent's NUnit result file.
        _process = Process.Start(start) ?? throw new AssertionException("Could not start isolated test.");
        _output = _process.StandardOutput.ReadToEndAsync();
        _errors = _process.StandardError.ReadToEndAsync();
    }

    public static void Publish(string stage, params string[] values) => Write(ChildDirectory, stage, values);
    public void Send(string stage, params string[] values) => Write(_directory, stage, values);
    public static Task<string[]> Receive(string stage) => Read(ChildDirectory, stage, null);
    public Task<string[]> Read(string stage) => Read(_directory, stage, _process);
    private static string ChildDirectory => Environment.GetEnvironmentVariable(DirectoryVariable)
        ?? throw new AssertionException("Not an isolated test child.");

    private static void Write(string directory, string stage, string[] values)
    {
        string target = Path.Combine(directory, stage);
        string temporary = target + ".tmp";
        File.WriteAllLines(temporary, values);
        File.Move(temporary, target);
    }

    private static async Task<string[]> Read(string directory, string stage, Process? process)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        string path = Path.Combine(directory, stage);
        while (!File.Exists(path))
        {
            if (process?.HasExited == true) throw new AssertionException($"Child exited {process.ExitCode} before {stage}.");
            await Task.Delay(20, deadline.Token);
        }
        return await File.ReadAllLinesAsync(path, deadline.Token);
    }

    public async Task Kill()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await Task.WhenAll(_output, _errors);
    }

    public async Task WaitForSuccess()
    {
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert.That(_process.ExitCode, Is.Zero, $"{await _output}\n{await _errors}");
    }

    public async ValueTask DisposeAsync()
    {
        try { await Kill(); }
        finally
        {
            TestContext.Out.WriteLine($"Isolated child {_process.Id}: exit {_process.ExitCode}\n{await _output}\n{await _errors}");
            _process.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
