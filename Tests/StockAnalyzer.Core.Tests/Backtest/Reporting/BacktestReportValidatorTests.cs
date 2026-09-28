using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

public class BacktestReportValidatorTests
{
    [Fact]
    public void Validate_DefaultReport_RejectsMissingMetricSlots()
    {
        Assert.Throws<ArgumentException>(() => BacktestReportValidator.Validate(new BacktestReport()));
    }

    [Fact]
    public void Validate_GeneratedReport_AcceptsAllEighteenSlots()
    {
        BacktestReport report = new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, new[] { 120m, 90m, 108m }),
            ReportTestHelpers.Options());

        BacktestReportValidator.Validate(report);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(367)]
    public void Options_AnnualPeriodsOutsideSupportedRange_IsRejected(int annualPeriods)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BacktestReportOptions(
            StockAnalyzer.Core.Models.TimeFrame.D1,
            0,
            ReportTestHelpers.BaseUtc,
            ReportTestHelpers.BaseUtc.AddDays(1))
        {
            AnnualPeriods = annualPeriods,
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(366)]
    public void Options_AnnualPeriodsSupportedBoundary_IsAccepted(int annualPeriods)
    {
        var options = new BacktestReportOptions(
            StockAnalyzer.Core.Models.TimeFrame.D1,
            0,
            ReportTestHelpers.BaseUtc,
            ReportTestHelpers.BaseUtc.AddDays(1))
        {
            AnnualPeriods = annualPeriods,
        };

        Assert.Equal(annualPeriods, options.AnnualPeriods);
    }

    [Fact]
    public void Validate_UndefinedMetricEnum_IsRejected()
    {
        JsonObject json = ValidReportJson();
        json[nameof(BacktestReport.TotalPnL)]![nameof(MetricValue.Status)] = int.MaxValue;

        Assert.Throws<ArgumentException>(() => BacktestReportValidator.Validate(Deserialize(json)));
    }

    [Fact]
    public void Validate_ConfidenceIntervalForNonValidAdjustedSortino_IsRejected()
    {
        JsonObject json = ValidReportJson();
        json[nameof(BacktestReport.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval)] = new JsonObject
        {
            [nameof(ConfidenceInterval.Lower)] = 0m,
            [nameof(ConfidenceInterval.Upper)] = 1m,
        };

        Assert.Throws<ArgumentException>(() => BacktestReportValidator.Validate(Deserialize(json)));
    }

    private static JsonObject ValidReportJson()
    {
        BacktestReport report = new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, new[] { 120m, 90m, 108m }),
            ReportTestHelpers.Options());
        return JsonNode.Parse(JsonSerializer.Serialize(report))!.AsObject();
    }

    private static BacktestReport Deserialize(JsonObject json) =>
        JsonSerializer.Deserialize<BacktestReport>(json.ToJsonString())!;
}
