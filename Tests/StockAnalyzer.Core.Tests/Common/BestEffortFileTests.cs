using System;
using System.IO;
using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

public sealed class BestEffortFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_best_effort_file_" + Guid.NewGuid().ToString("N"));

    public BestEffortFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void TryDelete_ExistingFile_RemovesIt()
    {
        string path = Path.Combine(_root, "present.tmp");
        File.WriteAllText(path, "x");

        BestEffortFile.TryDelete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryDelete_MissingFile_DoesNotThrow()
    {
        BestEffortFile.TryDelete(Path.Combine(_root, "never_existed.tmp"));
    }

    [Fact]
    public void TryDelete_FileHeldOpenByAnotherHandle_DoesNotThrow()
    {
        // On Windows the open handle makes the delete fail with an IOException, on other systems the delete succeeds; neither may surface.
        string path = Path.Combine(_root, "held.tmp");
        using var held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        BestEffortFile.TryDelete(path);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(path));
        }
    }
}
