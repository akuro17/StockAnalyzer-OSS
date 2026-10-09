#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Runs one of the test assets under <c>Assets/*.py</c> with the managed RL interpreter and returns its output. It is not a test class: the opt-in
/// tests that use it already run in a collection with disabled parallelization (the portfolio smoke tests start the trainer in the same way), so the
/// child never runs beside another test.
/// </summary>
internal static class RlPortfolioPythonScript
{
    private const string TrainerScript = "training/train_rl_portfolio.py";

    /// <summary>The trainer's folder (the self-check script imports the trainer from there).</summary>
    public static string TrainingDirectory => Path.GetDirectoryName(RlWorkerProcess.ResolveInterpreter(TrainerScript).Script)!;

    public static Task<(int ExitCode, string Output, string Errors)> RunAsync(string assetName, params string[] arguments)
        => RunInDirectoryAsync(null, assetName, arguments);

    /// <summary>Same as <see cref="RunAsync"/> with the child's working directory (null = this process's).</summary>
    public static async Task<(int ExitCode, string Output, string Errors)> RunInDirectoryAsync(string? workingDirectory, string assetName, params string[] arguments)
    {
        var (_, _, python) = RlWorkerProcess.ResolveInterpreter(TrainerScript);
        string script = Path.Combine(AppContext.BaseDirectory, "Assets", assetName);
        if (!File.Exists(python) || !File.Exists(script))
            throw new FileNotFoundException("The managed RL interpreter or the test asset is unavailable: " + python + " / " + script);
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(script);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The interpreter did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(RlContract.JobDeadlineMinutes));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await errors);
    }
}
