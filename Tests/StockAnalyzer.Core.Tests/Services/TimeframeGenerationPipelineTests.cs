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
/// W3: the weekly and monthly files follow every daily update. <c>generate_timeframes.py</c> checks its own result (<c>--selfcheck</c>: failures are
/// counted into the exit code, what was written is read back and compared with a re-aggregation of the daily file), and <c>update_pipeline.py</c> runs
/// it also when the daily update failed. The pipeline is run here with stub sub-scripts, so only its decisions are tested (opt-in: starts Python).
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

    [ChildProcessFact]
    public async Task TheGeneratorChecksItsOwnFailureAndVerificationRules()
    {
        string root = RepositoryRoot();

        (int exitCode, string output) = await RunAsync(Python(root), root, Path.Combine(root, "StockAnalyzer.Python", "generate_timeframes.py"), "--selfcheck");

        Assert.True(exitCode == 0, output);
        Assert.Contains("SELFCHECK OK", output);
    }

    [ChildProcessFact]
    public async Task TheGeneratorExitsNonZeroWhenATickerHasNoDailyFile()
    {
        string root = RepositoryRoot();
        string workdir = Path.Combine(Path.GetTempPath(), "sa_tf_gen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);
        try
        {
            // The real script, started for a ticker whose daily file does not exist in the real Data/Daily: nothing is written, the exit code says so.
            string ticker = "NOSUCHTICKER" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

            (int exitCode, string output) = await RunAsync(Python(root), workdir, Path.Combine(root, "StockAnalyzer.Python", "generate_timeframes.py"), "--ticker", ticker);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("FAILED", output);
            Assert.False(File.Exists(Path.Combine(root, "Data", "Weekly", ticker + ".parquet")));
            Assert.False(File.Exists(Path.Combine(root, "Data", "Monthly", ticker + ".parquet")));
        }
        finally
        {
            try { Directory.Delete(workdir, true); } catch (IOException) { }
        }
    }

    [ChildProcessTheory]
    [InlineData(0, 0, 0)] // both fine
    [InlineData(1, 0, 1)] // the daily update failed: the generator still ran, the pipeline reports the failure
    [InlineData(0, 1, 1)] // the generator failed: the pipeline reports it
    [InlineData(1, 1, 1)] // both failed
    public async Task ThePipelineRunsTheGeneratorAfterAFailedDailyUpdateAndFailsIfEitherFailed(int dailyExit, int generatorExit, int expectedExit)
    {
        string root = RepositoryRoot();
        string folder = Path.Combine(Path.GetTempPath(), "sa_tf_pipe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(Path.Combine(root, "StockAnalyzer.Python", "update_pipeline.py"), Path.Combine(folder, "update_pipeline.py"));
            File.WriteAllText(Path.Combine(folder, "update_daily.py"), $"import sys\nsys.exit({dailyExit})\n");
            File.WriteAllText(Path.Combine(folder, "generate_timeframes.py"),
                $"import sys\nopen(r'{Path.Combine(folder, GeneratorRanMarker)}', 'w').write('ran')\nsys.exit({generatorExit})\n");

            (int exitCode, string output) = await RunAsync(Python(root), folder, Path.Combine(folder, "update_pipeline.py"), "--ticker", "AAA");

            Assert.True(expectedExit == exitCode, output);
            Assert.True(File.Exists(Path.Combine(folder, GeneratorRanMarker)), "the generator must run whatever the daily update returned");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
