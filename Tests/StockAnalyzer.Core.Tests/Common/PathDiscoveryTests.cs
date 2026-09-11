using System;
using System.IO;
using Xunit;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Tests.Common;

public class PathDiscoveryTests
{
    [Fact]
    public void ResolveConfigPath_ShouldReturnValidPathAndNotThrow()
    {
        // Act
        var result = PathDiscovery.ResolveConfigPath("test_config.json");

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.True(Path.IsPathRooted(result));
        Assert.Equal("test_config.json", Path.GetFileName(result));
    }

    [Fact]
    public void ResolvePortfolioPath_ShouldReturnValidPathAndNotThrow()
    {
        // Act
        var result = PathDiscovery.ResolvePortfolioPath("test_portfolio.json");

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.True(Path.IsPathRooted(result));
        Assert.Equal("test_portfolio.json", Path.GetFileName(result));
    }

    [Theory]
    [InlineData("Data/Daily", "Daily")]
    [InlineData("Data/Metadata", "Metadata")]
    [InlineData("../Data/Daily", "Daily")]
    [InlineData("..\\Data\\Metadata", "Metadata")]
    public void ResolveDataPath_ShouldReturnValidRootedPathEndingWithSubfolder(string inputPath, string expectedSubfolder)
    {
        // Act
        var result = PathDiscovery.ResolveDataPath(inputPath, "Data/Daily");

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.True(Path.IsPathRooted(result));
        Assert.EndsWith(expectedSubfolder, result.Replace('\\', '/'));
    }

    [Fact]
    public void ResolveTemplatesDirectory_ShouldReturnValidRootedDirectory()
    {
        // Act
        var rootTemplatesDir = PathDiscovery.ResolveTemplatesDirectory();
        var indicatorTemplatesDir = PathDiscovery.ResolveTemplatesDirectory(StockAnalyzer.Core.Models.Templates.TemplateType.Indicator);
        var columnTemplatesDir = PathDiscovery.ResolveTemplatesDirectory(StockAnalyzer.Core.Models.Templates.TemplateType.Column);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(rootTemplatesDir));
        Assert.True(Path.IsPathRooted(rootTemplatesDir));
        Assert.EndsWith("Templates", rootTemplatesDir.Replace('\\', '/'));

        Assert.False(string.IsNullOrWhiteSpace(indicatorTemplatesDir));
        Assert.True(Path.IsPathRooted(indicatorTemplatesDir));
        Assert.EndsWith("Templates/Indicator", indicatorTemplatesDir.Replace('\\', '/'));

        Assert.False(string.IsNullOrWhiteSpace(columnTemplatesDir));
        Assert.True(Path.IsPathRooted(columnTemplatesDir));
        Assert.EndsWith("Templates/Column", columnTemplatesDir.Replace('\\', '/'));
    }

    [Fact]
    public void ResolveTemplatePath_ShouldReturnValidFilePathUnderTypeFolder()
    {
        // Act
        var guid = Guid.NewGuid().ToString("N") + ".json";
        var path = PathDiscovery.ResolveTemplatePath(StockAnalyzer.Core.Models.Templates.TemplateType.Indicator, guid);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(Path.IsPathRooted(path));
        Assert.Equal(guid, Path.GetFileName(path));
        Assert.EndsWith($"Templates/Indicator/{guid}", path.Replace('\\', '/'));
    }

    [Fact]
    public void ResolvePredictionModelPath_AbsolutePath_ReturnedVerbatim()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "sa_pred_" + Guid.NewGuid().ToString("N") + ".onnx");

        var result = PathDiscovery.ResolvePredictionModelPath(absolute);

        Assert.Equal(absolute, result);
    }

    [Theory]
    [InlineData("trend_predictor.onnx")]
    [InlineData("Models/trend_predictor.onnx")]
    [InlineData("")]
    public void ResolvePredictionModelPath_NoExistingFile_ResolvesUnderDataModels(string configured)
    {
        var result = PathDiscovery.ResolvePredictionModelPath(configured);

        Assert.True(Path.IsPathRooted(result));
        var dir = Path.GetDirectoryName(result)!;
        Assert.Equal("Models", Path.GetFileName(dir));
        Assert.True(Directory.Exists(dir), "Models directory must be created on resolve.");
        Assert.Equal("trend_predictor.onnx", Path.GetFileName(result));
    }

    [Fact]
    public void ResolvePredictionModelPath_ExistingBinRelativeFile_TakesPrecedence()
    {
        // Probe file sits directly in BaseDirectory (always present, writable in tests)
        // so the test never has to create or remove a directory that also holds fixtures.
        var probeName = "__pred_probe_" + Guid.NewGuid().ToString("N") + ".onnx";
        var probePath = Path.Combine(AppContext.BaseDirectory, probeName);
        File.WriteAllText(probePath, "probe");
        try
        {
            var result = PathDiscovery.ResolvePredictionModelPath(probeName);

            Assert.Equal(Path.GetFullPath(probePath), Path.GetFullPath(result));
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    [Theory]
    [InlineData("../../evil.onnx")]
    [InlineData("..\\..\\evil.onnx")]
    public void ResolvePredictionModelPath_TraversalFilename_StaysInsideModelsDir(string configured)
    {
        var result = Path.GetFullPath(PathDiscovery.ResolvePredictionModelPath(configured));
        var modelsDir = Path.GetFullPath(Path.GetDirectoryName(
            PathDiscovery.ResolvePredictionModelPath("trend_predictor.onnx"))!);

        Assert.StartsWith(modelsDir, result);
        Assert.Equal("evil.onnx", Path.GetFileName(result));
    }
}
