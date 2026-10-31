using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// W3: the weekly and monthly files follow every daily update, written by the C# <c>TimeFrameParquetGenerator</c> after <c>update_pipeline.py</c>.
/// There is no Python generator any more (owner decision 2026-10-31); <c>update_pipeline.py</c> reports the daily update only and must not run one. The
/// pipeline is run here with stub sub-scripts, so only its decisions are tested (opt-in: starts Python).
/// </summary>
[Collection("ChildProcess")]
public sealed class TimeframeGenerationPipelineTests
{
    private const string GeneratorRanMarker = "generator-ran.txt";

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root.FullName;
    }

    private static string Python(string root)
    {
        string python = Path.Combine(root, "StockAnalyzer.Python", ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python)) throw SkipException.ForSkip("The training Python environment is not installed.");
        return python;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string python, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python did not start.");
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var limit = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception killError) when (killError is InvalidOperationException or Win32Exception) { }
            throw new XunitException("Python timed out after 2 minutes.");
        }

        return (process.ExitCode, await output + await error);
    }

    [ChildProcessTheory]
    [InlineData(0, 0)] // the daily update worked: the pipeline succeeds
    [InlineData(1, 1)] // the daily update failed: the pipeline reports the failure
    public async Task ThePipelineReportsTheDailyUpdateAndNeverRunsThePythonGenerator(int dailyExit, int expectedExit)
    {
        string root = RepositoryRoot();
        string folder = Path.Combine(Path.GetTempPath(), "sa_tf_pipe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // The weekly and monthly files are written by the C# TimeFrameParquetGenerator after the pipeline; a generator script next to the pipeline must stay unused.
            foreach (string script in new[] { "update_pipeline.py", "symbol_validator.py" })
            {
                File.Copy(Path.Combine(root, "StockAnalyzer.Python", script), Path.Combine(folder, script));
            }

            File.WriteAllText(Path.Combine(folder, "update_daily.py"), $"import sys\nsys.exit({dailyExit})\n");
            File.WriteAllText(Path.Combine(folder, "generate_timeframes.py"),
                $"import sys\nopen(r'{Path.Combine(folder, GeneratorRanMarker)}', 'w').write('ran')\nsys.exit(0)\n");

            (int exitCode, string output) = await RunAsync(Python(root), folder, Path.Combine(folder, "update_pipeline.py"), "--ticker", "AAA");

            Assert.True(expectedExit == exitCode, output);
            Assert.False(File.Exists(Path.Combine(folder, GeneratorRanMarker)), "the pipeline must not run the Python generator (the C# generator writes the files)");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
