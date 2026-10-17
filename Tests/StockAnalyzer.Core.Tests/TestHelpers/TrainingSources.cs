using System;
using System.IO;
using Xunit;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>The text of a Python training source file, for tests that pin a name or a literal the C# side shares with it.</summary>
internal static class TrainingSources
{
    private const string SolutionFile = "StockAnalyzer.sln", PythonFolder = "StockAnalyzer.Python", TrainingFolder = "training";

    public static string Read(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFile))) directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, PythonFolder, TrainingFolder, fileName));
    }
}
