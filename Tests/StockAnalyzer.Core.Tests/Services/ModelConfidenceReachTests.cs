using System;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ModelConfidenceReachTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sa_reach_" + Guid.NewGuid().ToString("N"));

    public ModelConfidenceReachTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ } }

    private string Metrics(string json)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".onnx.metrics.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>A reach object over <paramref name="bins"/> bins with the given (bin, count) pairs; n = the sum unless given.</summary>
    private string Reach(int bins, (int Bin, int Count)[] filled, long? n = null)
    {
        var counts = new int[bins];
        foreach (var (bin, count) in filled) counts[bin] = count;
        return $"{{\"fold_macro_f1\":0.2,\"confidence_reach\":{{\"bins\":{bins},\"n\":{n ?? counts.Sum()},\"max_probability_histogram\":[{string.Join(",", counts)}]}}}}";
    }

    private static readonly (int, int)[] Hundred = { (33, 30), (36, 40), (45, 20), (50, 6), (62, 4) };   // 100 windows; 10 at or above 0.5

    [Theory]
    [InlineData(0.5f, 0.10)]      // 0.5 opens bin 50
    [InlineData(0.51f, 0.04)]     // bin 51 upwards: only the 0.62 windows
    [InlineData(0.505f, 0.04)]    // not a multiple of 1/100: rounded up to 0.51, so the share is a lower bound
    [InlineData(0.33f, 1.0)]      // everything is in a bin at or above 33
    [InlineData(0.34f, 0.70)]
    [InlineData(1.0f, 0.0)]       // nothing reaches 100 %
    [InlineData(0.01f, 1.0)]
    public void TheShareIsTheWindowsInTheBinsFromTheThresholdUpwards(float threshold, double expected)
        => Assert.Equal(expected, ModelConfidenceReach.ShareAtOrAbove(Metrics(Reach(100, Hundred)), threshold)!.Value, 12);

    [Theory]
    [InlineData(0.5f, 0.10)]      // a multiple of 1/100: both bounds agree
    [InlineData(0.51f, 0.04)]
    [InlineData(0.505f, 0.10)]    // between bins 50 and 51: every window of bin 50 could reach it
    [InlineData(0.501f, 0.10)]
    [InlineData(1.0f, 0.0)]       // the last bin is the highest one a window can be in: nothing there
    [InlineData(0.01f, 1.0)]
    public void TheUpperBoundCountsEveryWindowThatCouldReachTheThreshold(float threshold, double expected)
        => Assert.Equal(expected, ModelConfidenceReach.ShareUpperBound(Metrics(Reach(100, Hundred)), threshold)!.Value, 12);

    [Fact]
    public void AWindowAtProbabilityOneIsInTheLastBin_SoAThresholdOfOneHasAnUpperBoundAboveZero()
    {
        var path = Metrics(Reach(100, new[] { (36, 99), (99, 1) }));

        Assert.Equal(0.0, ModelConfidenceReach.ShareAtOrAbove(path, 1.0f)!.Value);       // the lower bound rounds up past the last bin
        Assert.Equal(0.01, ModelConfidenceReach.ShareUpperBound(path, 1.0f)!.Value, 12);
    }

    [Fact]
    public void TheUpperBoundIsNeverBelowTheLowerBound_AndIsUnknownWhereTheLowerBoundIs()
    {
        foreach (var threshold in new[] { 0.01f, 0.333f, 0.5f, 0.505f, 0.99f, 1.0f })
            Assert.True(ModelConfidenceReach.ShareUpperBound(Metrics(Reach(100, Hundred)), threshold) >= ModelConfidenceReach.ShareAtOrAbove(Metrics(Reach(100, Hundred)), threshold));
        Assert.Null(ModelConfidenceReach.ShareUpperBound(Metrics("{}"), 0.5f));
        Assert.Null(ModelConfidenceReach.ShareUpperBound(Path.Combine(_folder, "missing.json"), 0.5f));
        Assert.Null(ModelConfidenceReach.ShareUpperBound(Metrics(Reach(100, Hundred)), 0f));
    }

    [Fact]
    public void ANeverConfidentModelHasAShareOfExactlyZero()
        => Assert.Equal(0.0, ModelConfidenceReach.ShareAtOrAbove(Metrics(Reach(100, new[] { (33, 600), (40, 400) })), 0.5f));

    [Fact]
    public void AnotherBinCountIsReadFromTheFile()
    {
        var path = Metrics(Reach(10, new[] { (3, 6), (5, 3), (9, 1) }));       // bins of width 0.1: 0.5 opens bin 5
        Assert.Equal(0.4, ModelConfidenceReach.ShareAtOrAbove(path, 0.5f)!.Value, 12);
        Assert.Equal(0.1, ModelConfidenceReach.ShareAtOrAbove(path, 0.6f)!.Value, 12);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"confidence_reach\": 5}")]
    [InlineData("{\"confidence_reach\": {\"bins\": 100}}")]
    [InlineData("{\"confidence_reach\": {\"bins\": 0, \"n\": 1, \"max_probability_histogram\": []}}")]
    [InlineData("{\"confidence_reach\": {\"bins\": 1001, \"n\": 1, \"max_probability_histogram\": []}}")]
    [InlineData("{\"confidence_reach\": {\"bins\": 2, \"n\": 3, \"max_probability_histogram\": [1, 2, 0]}}")]     // length differs from bins
    [InlineData("{\"confidence_reach\": {\"bins\": 2, \"n\": 3, \"max_probability_histogram\": [1, -2]}}")]      // negative count
    [InlineData("{\"confidence_reach\": {\"bins\": 2, \"n\": 3, \"max_probability_histogram\": [1, 2.5]}}")]     // not an integer
    [InlineData("{\"confidence_reach\": {\"bins\": 2, \"n\": 3, \"max_probability_histogram\": [1, \"2\"]}}")]   // not a number
    [InlineData("{\"confidence_reach\": {\"bins\": 2, \"n\": 4, \"max_probability_histogram\": [1, 2]}}")]      // total differs from n
    [InlineData("{\"confidence_reach\": {\"bins\": 2, \"n\": 0, \"max_probability_histogram\": [0, 0]}}")]      // no windows
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public void WhatCannotBeReadIsUnknown_NotZero(string json)
        => Assert.Null(ModelConfidenceReach.ShareAtOrAbove(Metrics(json), 0.5f));

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.1f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void AThresholdOutsideZeroToOneIsUnknown(float threshold)
        => Assert.Null(ModelConfidenceReach.ShareAtOrAbove(Metrics(Reach(100, Hundred)), threshold));

    [Fact]
    public void NoFileIsUnknown()
        => Assert.Null(ModelConfidenceReach.ShareAtOrAbove(Path.Combine(_folder, "missing.json"), 0.5f));
}
