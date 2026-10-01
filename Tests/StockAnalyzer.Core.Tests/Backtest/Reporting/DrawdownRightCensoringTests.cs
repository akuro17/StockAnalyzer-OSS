using System;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// Right-censoring visibility of the two drawdown durations (Y:\Temp\sa_implementation_plan_BacktestDrawdownCensoringVisibility.md, T8):
/// the flags say whether a duration is complete or only a lower bound, and never change any metric value.
/// </summary>
public class DrawdownRightCensoringTests
{
    private static (DrawdownSeriesCalculator.DrawdownDurationResult Depth, DrawdownSeriesCalculator.DrawdownDurationResult Longest) Compute(params decimal[] equityIncludingInitial)
    {
        ImmutableArray<decimal> equity = equityIncludingInitial.ToImmutableArray();
        ImmutableArray<double> ratio = DrawdownSeriesCalculator.ComputeDrawdownRatioSeries(equity);
        return (
            DrawdownSeriesCalculator.ComputeMaxDepthDrawdownDuration(equity, ratio),
            DrawdownSeriesCalculator.ComputeLongestDrawdownDuration(equity));
    }

    [Fact]
    public void RecoveredDrawdown_IsNotCensored()
    {
        var (depth, longest) = Compute(100m, 90m, 100m);

        Assert.Equal(2m, depth.Value.Value);
        Assert.False(depth.RightCensored);
        Assert.Equal(2m, longest.Value.Value);
        Assert.False(longest.RightCensored);
    }

    [Fact]
    public void UnrecoveredMaxDepthEpisode_IsCensored_OnBothDurations()
    {
        // E = 100,95,90,92: the only episode never regains 100; counted to the last bar (3).
        var (depth, longest) = Compute(100m, 95m, 90m, 92m);

        Assert.Equal(3m, depth.Value.Value);
        Assert.True(depth.RightCensored);
        Assert.Equal(3m, longest.Value.Value);
        Assert.True(longest.RightCensored);
    }

    [Fact]
    public void UnrecoveredEpisodeShorterThanACompletedOne_LeavesTheLongestUncensored()
    {
        // E = 100,90,80,100,95: completed episode 3 bars (deepest); the open one (peak idx3, 1 bar) is shorter -> the longest is exact.
        var (depth, longest) = Compute(100m, 90m, 80m, 100m, 95m);

        Assert.Equal(3m, depth.Value.Value);
        Assert.False(depth.RightCensored);
        Assert.Equal(3m, longest.Value.Value);
        Assert.False(longest.RightCensored);
    }

    [Fact]
    public void UnrecoveredEpisodeAsLongAsACompletedOne_FlagsTheLongestConservatively()
    {
        // E = 100,90,100,95,95: completed episode 2 bars (deepest, recovered); the open one also spans 2 bars.
        var (depth, longest) = Compute(100m, 90m, 100m, 95m, 95m);

        Assert.False(depth.RightCensored);
        Assert.Equal(2m, longest.Value.Value);
        Assert.True(longest.RightCensored);
    }

    [Fact]
    public void UnrecoveredEpisodeLongerThanEveryCompletedOne_IsCensored()
    {
        // E = 100,90,100,95,94,93: completed 2 bars; the open one runs 3 bars (idx2 -> 5) and is the longest, but the deeper episode is the completed one.
        var (depth, longest) = Compute(100m, 90m, 100m, 95m, 94m, 93m);

        Assert.Equal(2m, depth.Value.Value);
        Assert.False(depth.RightCensored);
        Assert.Equal(3m, longest.Value.Value);
        Assert.True(longest.RightCensored);
    }

    [Fact]
    public void RecoveryExactlyOnTheLastBar_IsNotCensored()
    {
        var (depth, longest) = Compute(100m, 90m, 95m, 100m);

        Assert.Equal(3m, depth.Value.Value);
        Assert.False(depth.RightCensored);
        Assert.False(longest.RightCensored);
    }

    [Fact]
    public void NoDrawdown_IsNotCensored()
    {
        var (depth, longest) = Compute(100m, 101m, 101m, 103m);

        Assert.Equal(0m, depth.Value.Value);
        Assert.False(depth.RightCensored);
        Assert.Equal(0m, longest.Value.Value);
        Assert.False(longest.RightCensored);
    }

    [Fact]
    public void Flags_DoNotChangeTheMetricValues()
    {
        // The values are exactly what the pre-flag calculators returned (same path as DrawdownDurationAndExposureMetricsTests).
        var (depth, longest) = Compute(100m, 110m, 105m, 100m, 110m, 120m, 118m, 119m, 117m, 121m);

        Assert.Equal(MetricValue.Valid(3m, MetricUnit.Bars), depth.Value);
        Assert.Equal(MetricValue.Valid(4m, MetricUnit.Bars), longest.Value);
    }

    // ---- Report level -------------------------------------------------------------------------------------------------------------

    private static BacktestReport Generate(params decimal[] postWarmupEquity) => new BacktestReportGenerator().Generate(
        ReportTestHelpers.BuildResult(100m, postWarmupEquity), ReportTestHelpers.Options());

    [Fact]
    public void Report_CarriesTheFlagsOfItsValidDurations()
    {
        BacktestReport open = Generate(95m, 90m, 92m);
        BacktestReport closed = Generate(90m, 100m);

        Assert.True(open.MaxDepthDrawdownDurationRightCensored);
        Assert.True(open.LongestDrawdownDurationRightCensored);
        Assert.False(closed.MaxDepthDrawdownDurationRightCensored);
        Assert.False(closed.LongestDrawdownDurationRightCensored);
    }

    [Fact]
    public void Report_WithAnEmptySample_HasNoFlags_BecauseTheDurationsAreNotValid()
    {
        BacktestReport report = new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, Array.Empty<decimal>()), ReportTestHelpers.Options());

        Assert.NotEqual(MetricStatus.Valid, report.MaxDepthDrawdownDuration!.Value.Status);
        Assert.Null(report.MaxDepthDrawdownDurationRightCensored);
        Assert.Null(report.LongestDrawdownDurationRightCensored);
        BacktestReportValidator.Validate(report);
    }

    [Fact]
    public void Flags_SurviveAJsonRoundTrip()
    {
        BacktestReport original = Generate(95m, 90m, 92m);

        BacktestReport reloaded = JsonSerializer.Deserialize<BacktestReport>(JsonSerializer.Serialize(original))!;

        Assert.True(reloaded.MaxDepthDrawdownDurationRightCensored);
        Assert.True(reloaded.LongestDrawdownDurationRightCensored);
        BacktestReportValidator.Validate(reloaded);
    }

    [Fact]
    public void ReportsWithoutExtendedMembers_LoadAndValidateWithoutFlags()
    {
        BacktestReport legacy = ReportTestHelpers.ToLegacyReport(Generate(95m, 90m, 92m));

        Assert.Null(legacy.MaxDepthDrawdownDurationRightCensored);
        Assert.Null(legacy.LongestDrawdownDurationRightCensored);
        BacktestReportValidator.Validate(legacy);
    }

    // ---- Validator ---------------------------------------------------------------------------------------------------------------------

    private static JsonObject ValidJson() => JsonNode.Parse(JsonSerializer.Serialize(Generate(95m, 90m, 92m)))!.AsObject();

    [Theory]
    [InlineData("MaxDepthDrawdownDurationRightCensored")]
    [InlineData("LongestDrawdownDurationRightCensored")]
    public void ValidDuration_WithoutItsFlag_IsRejected(string flag)
    {
        JsonObject json = ValidJson();
        json.Remove(flag);

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => BacktestReportValidator.Validate(JsonSerializer.Deserialize<BacktestReport>(json.ToJsonString())!));
        Assert.Contains(flag, error.Message);
    }

    [Theory]
    [InlineData("MaxDepthDrawdownDuration", "MaxDepthDrawdownDurationRightCensored")]
    [InlineData("LongestDrawdownDuration", "LongestDrawdownDurationRightCensored")]
    public void FlagOnANonValidDuration_IsRejected(string metric, string flag)
    {
        JsonObject json = ValidJson();
        json[metric] = new JsonObject { ["Value"] = null, ["Status"] = 1, ["Unit"] = 5, ["Reason"] = 1 }; // InsufficientData / Bars / EmptyInput

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => BacktestReportValidator.Validate(JsonSerializer.Deserialize<BacktestReport>(json.ToJsonString())!));
        Assert.Contains(flag, error.Message);
    }

    [Fact]
    public void FlagWithoutAnyExtendedMetric_IsRejected()
    {
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(ReportTestHelpers.ToLegacyReport(Generate(95m, 90m, 92m))))!.AsObject();
        json["MaxDepthDrawdownDurationRightCensored"] = true;

        Assert.Throws<ArgumentException>(() => BacktestReportValidator.Validate(JsonSerializer.Deserialize<BacktestReport>(json.ToJsonString())!));
    }
}
