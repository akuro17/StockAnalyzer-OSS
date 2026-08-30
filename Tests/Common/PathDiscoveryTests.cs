using System.IO;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Templates;
using Xunit;

namespace StockAnalyzer.Tests.Common;

/// <summary>
/// Covers the defense-in-depth filename sanitization added to PathDiscovery's Resolve*Path
/// helpers: no current caller passes untrusted input, but the guard must still neutralize
/// path traversal and Windows-reserved device names without breaking normal filenames.
/// </summary>
public class PathDiscoverySanitizeFileNameTests
{
    [Fact]
    public void ResolveConfigPath_NormalFileName_IsUnchanged()
    {
        var result = PathDiscovery.ResolveConfigPath("normal_config.json");
        Assert.Equal("normal_config.json", Path.GetFileName(result));
    }

    [Fact]
    public void ResolveConfigPath_TraversalSequence_IsStripped()
    {
        var result = PathDiscovery.ResolveConfigPath("../../evil.json");
        var fileName = Path.GetFileName(result);
        Assert.DoesNotContain("..", fileName);
    }

    [Fact]
    public void ResolvePortfolioPath_TraversalSequence_CannotEscapePortfolioDirectory()
    {
        var portfolioDir = Path.GetDirectoryName(PathDiscovery.ResolvePortfolioPath("placeholder.json"));
        var result = PathDiscovery.ResolvePortfolioPath("..\\..\\..\\Windows\\System32\\evil.json");

        Assert.Equal(portfolioDir, Path.GetDirectoryName(result));
    }

    [Theory]
    [InlineData("CON.json", "CON")]
    [InlineData("con.json", "con")]
    [InlineData("PRN", "PRN")]
    [InlineData("COM1.json", "COM1")]
    [InlineData("LPT9.json", "LPT9")]
    public void ResolveTemplatePath_ReservedDeviceName_IsNeutralized(string reservedFileName, string reservedBaseName)
    {
        var result = PathDiscovery.ResolveTemplatePath(TemplateType.Indicator, reservedFileName);
        var actualBaseName = Path.GetFileNameWithoutExtension(result);

        Assert.NotEqual(reservedBaseName, actualBaseName, System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveTemplatePath_NormalFileName_IsUnchanged()
    {
        var result = PathDiscovery.ResolveTemplatePath(TemplateType.Column, "my_template.json");
        Assert.Equal("my_template.json", Path.GetFileName(result));
    }
}
