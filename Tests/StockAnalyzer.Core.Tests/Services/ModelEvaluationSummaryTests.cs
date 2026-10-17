using System;
using System.IO;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>One read of the metrics file gives the log-loss and the confidence histogram; the older single-purpose functions are views of it and must agree.</summary>
public sealed class ModelEvaluationSummaryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sa_summary_" + Guid.NewGuid().ToString("N"));
    private const string Classes = "Up,Down,Neutral";
    private const string Histogram = "\"confidence_reach\":{\"bins\":10,\"n\":10,\"max_probability_histogram\":[0,0,0,6,0,3,0,0,0,1]}";

    public ModelEvaluationSummaryTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ } }

    private string Metrics(string json)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".onnx.metrics.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void BothFactsComeFromOneFile()
    {
        var summary = ModelEvaluationSummary.Read(Metrics("{\"fold_multi_logloss\":1.2," + Histogram + "}"));

        Assert.Equal(1.2, summary.LogLoss);
        Assert.Equal(10, summary.Reach!.Bins);
        Assert.Equal(0.4, summary.Reach.ShareAtOrAbove(0.5f)!.Value, 12);
        Assert.True(ModelQualityCheck.IsNotBetterThanUniform(summary, Classes));
    }

    [Theory]
    [InlineData("{\"fold_multi_logloss\":1.2,\"confidence_reach\":{\"bins\":2}}", 1.2, false)]         // a damaged histogram does not hide the log-loss
    [InlineData("{\"fold_multi_logloss\":\"high\"," + Histogram + "}", null, true)]                   // a damaged log-loss does not hide the histogram
    [InlineData("{\"fold_multi_logloss\":null}", null, false)]
    [InlineData("{}", null, false)]
    [InlineData("[1,2]", null, false)]
    [InlineData("not json", null, false)]
    public void EachPartIsJudgedOnItsOwn(string json, double? logLoss, bool histogram)
    {
        var summary = ModelEvaluationSummary.Read(Metrics(json));

        Assert.Equal(logLoss, summary.LogLoss);
        Assert.Equal(histogram, summary.Reach is not null);
    }

    [Fact]
    public void AMissingFileIsUnknown_AndTheSingleFunctionsSayTheSame()
    {
        var path = Path.Combine(_folder, "missing.onnx.metrics.json");

        Assert.Equal(ModelEvaluationSummary.Unknown, ModelEvaluationSummary.Read(path));
        Assert.Null(ModelQualityCheck.IsNotBetterThanUniform(path, Classes));
        Assert.Null(ModelConfidenceReach.ShareAtOrAbove(path, 0.5f));
        Assert.Null(ModelConfidenceReach.ShareUpperBound(path, 0.5f));
    }

    [Theory]
    [InlineData("{\"fold_multi_logloss\":1.2," + Histogram + "}")]
    [InlineData("{\"fold_multi_logloss\":0.9," + Histogram + "}")]
    [InlineData("{" + Histogram + "}")]
    [InlineData("{\"fold_multi_logloss\":1.2}")]
    public void ThePathFunctionsAndTheSummaryGiveTheSameAnswers(string json)
    {
        var path = Metrics(json);
        var summary = ModelEvaluationSummary.Read(path);

        Assert.Equal(ModelQualityCheck.IsNotBetterThanUniform(path, Classes), ModelQualityCheck.IsNotBetterThanUniform(summary, Classes));
        foreach (var threshold in new[] { 0.1f, 0.5f, 0.55f, 0.9f, 1.0f })
        {
            Assert.Equal(ModelConfidenceReach.ShareAtOrAbove(path, threshold), summary.Reach?.ShareAtOrAbove(threshold));
            Assert.Equal(ModelConfidenceReach.ShareUpperBound(path, threshold), summary.Reach?.ShareUpperBound(threshold));
        }
        Assert.Equal(ModelAutoActivationGate.Evaluate(path, Classes, 0.5f), ModelAutoActivationGate.Evaluate(summary, Classes, 0.5f));
    }

    [Fact]
    public void FewerThanTwoClassesOrABadThresholdAreUnknown()
    {
        var summary = ModelEvaluationSummary.Read(Metrics("{\"fold_multi_logloss\":9," + Histogram + "}"));

        Assert.Null(ModelQualityCheck.IsNotBetterThanUniform(summary, ""));
        Assert.Null(ModelQualityCheck.IsNotBetterThanUniform(summary, "Up"));
        Assert.Null(summary.Reach!.ShareAtOrAbove(0f));
        Assert.Null(summary.Reach.ShareUpperBound(1.5f));
        Assert.Null(summary.Reach.ShareAtOrAbove(float.NaN));
    }
}
