// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
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

    public int ProcessId => _process.Id;
    public long CreationTime { get; }
    public bool HasTerminated => _process.WaitForExit(0);

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
        // Windows exposes an exact creation FILETIME. Unix StartTime is an
        // estimated wall-clock conversion and is unsuitable for exact comparison.
        CreationTime = OperatingSystem.IsWindows() ? _process.StartTime.ToFileTimeUtc() : 0;
        _output = _process.StandardOutput.ReadToEndAsync();
        _errors = _process.StandardError.ReadToEndAsync();
    }

    public static void Publish(string stage, params string[] values)
    {
        using Process current = Process.GetCurrentProcess();
        Write(ChildDirectory, stage, [current.Id.ToString(CultureInfo.InvariantCulture),
            (OperatingSystem.IsWindows() ? current.StartTime.ToFileTimeUtc() : 0).ToString(CultureInfo.InvariantCulture), .. values]);
    }
    public void Send(string stage, params string[] values) => Write(_directory, stage, values);
    public static Task<string[]> Receive(string stage) => Read(ChildDirectory, stage, null);
    public async Task<string[]> Read(string stage)
    {
        string[] report = await Read(_directory, stage, _process);
        Assert.That(report.Length, Is.GreaterThanOrEqualTo(2), "Every stage must identify its actual worker process.");
        AssertOwnsProcess(int.Parse(report[0], CultureInfo.InvariantCulture), long.Parse(report[1], CultureInfo.InvariantCulture));
        return report[2..];
    }

    public void AssertOwnsProcess(int processId, long creationTime)
    {
        Assert.That(processId, Is.EqualTo(ProcessId), "The acknowledged worker must be the process this harness launched, not an untracked descendant.");
        Assert.That(creationTime, Is.EqualTo(CreationTime), "The acknowledged worker must be the same retained process incarnation.");
        TestContext.Out.WriteLine($"Acknowledged process {processId}, creation FILETIME {creationTime}.");
    }
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
        // Kill only the retained, directly launched process. Stage acknowledgements
        // prove that it is also the worker; no PID lookup or descendant sweep is used.
        if (!_process.HasExited) _process.Kill();
        await WaitForTermination(15_000);
        await Task.WhenAll(_output, _errors).WaitAsync(TimeSpan.FromSeconds(15));
    }

    public async Task WaitForSuccess()
    {
        await WaitForTermination(45_000);
        Assert.That(_process.ExitCode, Is.Zero, $"{await _output}\n{await _errors}");
    }

    private async Task WaitForTermination(int milliseconds)
    {
        // On Windows HasExited/WaitForExitAsync can observe an exit code before the
        // kernel process object signals. WaitForExit(int) waits on that retained
        // handle, the same termination boundary used by production key recovery.
        Assert.That(await Task.Run(() => _process.WaitForExit(milliseconds)), Is.True,
            $"Process {ProcessId} did not reach kernel termination within {milliseconds} ms.");
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
