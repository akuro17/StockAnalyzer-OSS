using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using Xunit;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class StrictOnnxVerifierTests
{
    [Fact]
    public async Task StrictVerifier_RejectsBadContractsAndChecksRealClassificationAndRegression()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var python = Path.Combine(root.FullName, "StockAnalyzer.Python", ".venv",
            OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python))
        {
            throw SkipException.ForSkip("The training Python virtual environment is not installed.");
        }

        var cases = Path.Combine(root.FullName, "Tests", "StockAnalyzer.Core.Tests", "Assets",
            "check_verify_model_strict.py");
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = root.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(cases);
        start.Environment["SA_EXPECTED_INDICATOR_NAMES"] = JsonSerializer.Serialize(Enum.GetNames<IndicatorType>());
        var output = await TrainingPythonTestRunner.RunAsync(start, TimeSpan.FromMinutes(5));
        Assert.Contains("strict ONNX verifier integration cases: PASS", output);
    }

    [Fact]
    public async Task PythonRunner_DrainsBothStreamsBeforeWaitingForExit()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var python = Path.Combine(root.FullName, "StockAnalyzer.Python", ".venv",
            OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python))
            throw SkipException.ForSkip("The training Python virtual environment is not installed.");

        var start = new ProcessStartInfo(python) { WorkingDirectory = root.FullName };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import sys; sys.stdout.write('x' * 1048576 + 'STDOUT_DONE\\n'); sys.stdout.flush(); sys.stderr.write('y' * 1048576 + 'STDERR_DONE\\n'); sys.stderr.flush()");
        var output = await TrainingPythonTestRunner.RunAsync(start, TimeSpan.FromMinutes(1));
        Assert.Contains("STDOUT_DONE", output);
    }

    [Fact]
    public async Task PythonRunner_TimeoutWaitsForOwnedChildExit()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var python = Path.Combine(root.FullName, "StockAnalyzer.Python", ".venv",
            OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python))
            throw SkipException.ForSkip("The training Python virtual environment is not installed.");

        var pidPath = Path.Combine(Path.GetTempPath(), "sa_python_runner_" + Guid.NewGuid().ToString("N") + ".pid");
        try
        {
            var start = new ProcessStartInfo(python) { WorkingDirectory = root.FullName };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import os,sys,time; open(sys.argv[1], 'w').write(str(os.getpid())); print('READY', flush=True); time.sleep(60)");
            start.ArgumentList.Add(pidPath);
            var failure = await Assert.ThrowsAnyAsync<XunitException>(() =>
                TrainingPythonTestRunner.RunAsync(start, TimeSpan.FromSeconds(5)));
            Assert.Contains("timed out", failure.Message);
            Assert.True(File.Exists(pidPath));
            var pid = int.Parse(File.ReadAllText(pidPath), CultureInfo.InvariantCulture);
            try
            {
                using var owned = Process.GetProcessById(pid);
                Assert.True(owned.HasExited);
            }
            catch (ArgumentException)
            {
                // The process has already been removed from the process table.
            }
        }
        finally
        {
            if (File.Exists(pidPath)) File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task PythonRunner_TimeoutAlsoBoundsDrainsAfterParentExit()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var python = Path.Combine(root.FullName, "StockAnalyzer.Python", ".venv",
            OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python))
            throw SkipException.ForSkip("The training Python virtual environment is not installed.");

        var pidPath = Path.Combine(Path.GetTempPath(), "sa_python_held_pipe_" + Guid.NewGuid().ToString("N") + ".pid");
        var start = new ProcessStartInfo(python) { WorkingDirectory = root.FullName };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(
            "import subprocess,sys; child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(20)'], " +
            "stdout=sys.stdout, stderr=sys.stderr); open(sys.argv[1], 'w').write(str(child.pid)); " +
            "print('PARENT_DONE', flush=True)");
        start.ArgumentList.Add(pidPath);
        var attempt = TrainingPythonTestRunner.RunAsync(start, TimeSpan.FromSeconds(1));
        try
        {
            var completed = await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(attempt, completed);
            var failure = await Assert.ThrowsAnyAsync<XunitException>(() => attempt);
            Assert.Contains("timed out", failure.Message);
            Assert.Contains("PARENT_DONE", failure.Message);
        }
        finally
        {
            if (File.Exists(pidPath))
            {
                var pid = int.Parse(File.ReadAllText(pidPath), CultureInfo.InvariantCulture);
                try
                {
                    using var child = Process.GetProcessById(pid);
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit(5000);
                    }
                }
                catch (ArgumentException)
                {
                    // The descendant has already left the process table.
                }
                File.Delete(pidPath);
            }
        }
    }
}
