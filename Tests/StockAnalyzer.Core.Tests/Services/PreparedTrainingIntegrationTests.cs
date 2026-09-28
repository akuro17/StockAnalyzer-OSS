using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class PreparedTrainingIntegrationTests
{
    [Fact]
    public async Task WeeklyAndMonthlyPreparedInputs_TrainAndVerifyRealOnnx()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var python = Path.Combine(root.FullName, "StockAnalyzer.Python", ".venv",
            OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python))
            throw SkipException.ForSkip("The training Python environment is not installed.");
        var cases = Path.Combine(root.FullName, "Tests", "StockAnalyzer.Core.Tests", "Assets",
            "check_prepared_training.py");
        var info = new ProcessStartInfo(python)
        {
            WorkingDirectory = root.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(cases);
        info.Environment["OMP_NUM_THREADS"] = "1";
        info.Environment["TF_NUM_INTRAOP_THREADS"] = "1";
        info.Environment["TF_NUM_INTEROP_THREADS"] = "1";
        info.Environment["TF_CPP_MIN_LOG_LEVEL"] = "3";
        var output = await TrainingPythonTestRunner.RunAsync(info, TimeSpan.FromMinutes(10));
        Assert.Contains("prepared monthly/tensorflow: PASS", output);
    }
}
