using System;
using System.IO;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ModelQualityCheckTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sa_quality_" + Guid.NewGuid().ToString("N"));
    private const string Classes = "Up,Down,Neutral";

    public ModelQualityCheckTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ } }

    private string Metrics(string json)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".onnx.metrics.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Theory]
    [InlineData("1.1017", true)]    // the default AAPL models: above ln 3 = 1.0986
    [InlineData("1.0986122886681098", true)]   // exactly the uniform guess is not better
    [InlineData("1.05", false)]
    [InlineData("0.4", false)]
    public void ALogLossAtOrAboveTheUniformGuess_IsNotBetterThanGuessing(string logLoss, bool expected)
    {
        var path = Metrics($"{{\"fold_multi_logloss\": {logLoss}, \"fold_macro_f1\": 0.16}}");

        Assert.Equal(expected, ModelQualityCheck.IsNotBetterThanUniform(path, Classes));
    }

    [Fact]
    public void TheUniformGuessDependsOnTheNumberOfClasses()
    {
        var path = Metrics("{\"fold_multi_logloss\": 0.8}");

        Assert.Equal(false, ModelQualityCheck.IsNotBetterThanUniform(path, Classes));        // below ln 3
        Assert.Equal(true, ModelQualityCheck.IsNotBetterThanUniform(path, "Up,Down"));       // 0.8 is above ln 2 = 0.693
        Assert.Equal(Math.Log(3), ModelQualityCheck.UniformLogLoss(3), 12);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"fold_multi_logloss\": \"high\"}")]
    [InlineData("{\"fold_multi_logloss\": null}")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public void WhatCannotBeRead_IsUnknown_NotAWarning(string json)
    {
        Assert.Null(ModelQualityCheck.IsNotBetterThanUniform(Metrics(json), Classes));
    }

    [Fact]
    public void NoFile_NoClasses_OrOneClass_IsUnknown()
    {
        var path = Metrics("{\"fold_multi_logloss\": 5}");

        Assert.Null(ModelQualityCheck.IsNotBetterThanUniform(Path.Combine(_folder, "missing.json"), Classes));
        Assert.Null(ModelQualityCheck.IsNotBetterThanUniform(path, ""));
        Assert.Null(ModelQualityCheck.IsNotBetterThanUniform(path, "Up"));
    }
}
