using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// The default model input length exists in C# (<see cref="PredictionSettings.DefaultWindowSize"/>), in the Python trainers
/// (<c>dataset.DEFAULT_WINDOW</c>) and in the shipped appsettings.example.json. A prediction needs the model window to equal
/// the prediction window, so a default changed in only one place makes a freshly trained model unusable.
/// </summary>
public class PredictionWindowDefaultParityTests
{
    private static readonly Regex PythonDefaultWindow = new(@"^DEFAULT_WINDOW\s*:\s*int\s*=\s*(?<value>\d+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void PythonDefaultWindow_EqualsTheCSharpDefault()
    {
        string source = File.ReadAllText(Path.Combine(TestRepositoryRoot.Find(), "StockAnalyzer.Python", "training", "dataset.py"));

        var match = PythonDefaultWindow.Match(source);

        Assert.True(match.Success, "DEFAULT_WINDOW assignment not found in dataset.py.");
        Assert.Equal(PredictionSettings.DefaultWindowSize, int.Parse(match.Groups["value"].Value));
    }

    [Fact]
    public void ShippedAppSettingsExample_UsesTheCSharpDefaultWindow()
    {
        string path = Path.Combine(TestRepositoryRoot.Find(), "StockAnalyzer.Avalonia", "appsettings.example.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        int configured = document.RootElement.GetProperty("Prediction").GetProperty("WindowSize").GetInt32();

        Assert.Equal(PredictionSettings.DefaultWindowSize, configured);
    }
}
