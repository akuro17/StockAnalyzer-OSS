using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ComposedTrainerIntegrationTests
{
    [Fact]
    public async Task LightGbmAndTensorFlow_TrainConvertAndPassStrictDiagnosis()
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
            "check_composed_trainers.py");
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = root.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(cases);
        var output = await TrainingPythonTestRunner.RunAsync(start, TimeSpan.FromMinutes(5));
        Assert.Contains("composed trainers end-to-end cases: PASS", output);
    }
}
