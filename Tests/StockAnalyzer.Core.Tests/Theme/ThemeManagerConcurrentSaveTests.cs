using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Core.Tests.Theme;

/// <summary>
/// SetThemeMode saves fire-and-forget, so several saves of the same file can overlap. Each one must complete without the
/// "&lt;file&gt;.tmp" collision (which ThemeManager only reports on the console) and leave a valid file (IoHydrationWebAiReview, T3).
/// </summary>
[Collection("ConsoleOutput")]
public sealed class ThemeManagerConcurrentSaveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"theme-manager-concurrent-{Guid.NewGuid():N}");

    public ThemeManagerConcurrentSaveTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task OverlappingSaves_AllSucceed_AndLeaveAValidFile()
    {
        string path = Path.Combine(_directory, "user_theme.json");
        var manager = new ThemeManager(null, path);
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(manager.SaveAsync)));
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.DoesNotContain("Failed to save theme", captured.ToString());
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }
}

[CollectionDefinition("ConsoleOutput", DisableParallelization = true)]
public sealed class ConsoleOutputCollection { }
