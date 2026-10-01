using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;
using static StockAnalyzer.Tests.Backtest.BacktestReportTestFactory;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Value formats, lower bounds, non-Valid display and locale coverage of the grouped Results-tab metric rows (fixed and extended metrics alike).</summary>
public class BacktestMetricRowFormattingTests
{
    private static readonly string[] CanonicalNames =
    {
        "GrossProfit", "GrossLoss", "AverageWin", "AverageLoss", "PayoffRatio", "LargestWin", "LargestLoss",
        "AverageHoldingPeriod", "MaxConsecutiveWins", "MaxConsecutiveLosses",
        "MaxDepthDrawdownDuration", "LongestDrawdownDuration", "TimeInMarket", "Exposure", "ExposureAdjustedCAGR",
    };

    private static ImmutableArray<BacktestMetricDisplayRow> Rows(BacktestReport report, ILocalizationService localization) =>
        BacktestMetricGroupBuilder.Build(report, localization).SelectMany(g => g.Rows).ToImmutableArray();

    private static BacktestMetricDisplayRow RowOf(ImmutableArray<BacktestMetricDisplayRow> rows, string name) =>
        rows.Single(r => r.LabelKey == "Backtest_Metric_" + name);

    // ---- fixed metrics: the formats of the original metrics table ----------------------------------

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void Build_FixedMetrics_UseTheirOriginalFormats(string cultureName)
    {
        BacktestReport report = WithMembers(GenerateReport(10m, -4m), new Dictionary<string, JsonNode?>
        {
            ["TotalPnL"] = Metric(1234.5m, MetricUnit.Currency),
            ["TotalReturn"] = Metric(0.1234m, MetricUnit.ReturnRatio),
            ["MaxDrawdown"] = Metric(0.05m, MetricUnit.DrawdownRatio),
            ["WinRate"] = Metric(0.5m, MetricUnit.WinRateRatio),
            ["AnnualizedSharpe"] = Metric(1.42837m, MetricUnit.Dimensionless),
            ["UlcerIndex"] = Metric(7.8m, MetricUnit.PercentPoints),
        });

        ImmutableArray<BacktestMetricDisplayRow> rows = WithCulture(cultureName, () => Rows(report, NullLocalizationService.Instance));

        Assert.Equal("1234.5000", RowOf(rows, "TotalPnL").ValueText);
        Assert.Equal("12.34%", RowOf(rows, "TotalReturn").ValueText);
        Assert.Equal("5.00%", RowOf(rows, "MaxDrawdown").ValueText);
        Assert.Equal("50.00%", RowOf(rows, "WinRate").ValueText);
        Assert.Equal("1.4284", RowOf(rows, "AnnualizedSharpe").ValueText);
        Assert.Equal("7.8000", RowOf(rows, "UlcerIndex").ValueText);
    }

    [Fact]
    public void Build_SortinoConfidenceInterval_IsShownOnlyWhenPresent()
    {
        BacktestReport withInterval = WithMembers(GenerateReport(10m, -4m), new Dictionary<string, JsonNode?>
        {
            ["AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval"] = new JsonObject { ["Lower"] = 1.25m, ["Upper"] = 2.5m },
        });
        BacktestReport withoutInterval = WithMembers(GenerateReport(10m, -4m), new Dictionary<string, JsonNode?>
        {
            ["AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval"] = null,
        });

        string? shown = WithCulture("en-US", () => RowOf(Rows(withInterval, NullLocalizationService.Instance), "AnnualizedSortinoAutocorrelationAdjusted").ConfidenceIntervalText);
        string? missing = RowOf(Rows(withoutInterval, NullLocalizationService.Instance), "AnnualizedSortinoAutocorrelationAdjusted").ConfidenceIntervalText;

        Assert.Equal("[1.2500, 2.5000]", shown);
        Assert.Equal("Backtest_Metric_NotAvailable", missing);
        Assert.All(
            Rows(withInterval, NullLocalizationService.Instance).Where(r => !r.LabelKey.EndsWith("AnnualizedSortinoAutocorrelationAdjusted", StringComparison.Ordinal)),
            r => Assert.Null(r.ConfidenceIntervalText));
    }

    [Fact]
    public void Build_FixedMetricWithoutATrade_IsNotAvailableWithAStatusTooltipOnly()
    {
        FakeLocalizationService locale = LoadLocale("en");

        ImmutableArray<BacktestMetricDisplayRow> rows = Rows(GenerateReport(), locale);

        BacktestMetricDisplayRow profitFactor = RowOf(rows, "ProfitFactor");
        Assert.NotEqual(MetricStatus.Valid, profitFactor.Status);
        Assert.Equal(locale.GetString("Backtest_Metric_NotAvailable"), profitFactor.ValueText);
        Assert.Equal(BacktestMetricSemantic.Neutral, profitFactor.Semantic);
        Assert.Contains(locale.GetString("Backtest_Metric_Status_" + profitFactor.Status), profitFactor.TooltipText, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ValidFixedMetric_HasNoTooltip()
    {
        ImmutableArray<BacktestMetricDisplayRow> rows = Rows(GenerateReport(10m, -4m), LoadLocale("en"));

        Assert.Null(RowOf(rows, "TotalPnL").TooltipText);
    }

    // ---- T2 -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void Build_ValidValues_UseTheFormatOfTheirMetric(string cultureName)
    {
        BacktestReport report = WithMembers(GenerateReport(10m, -4m), new Dictionary<string, JsonNode?>
        {
            ["GrossProfit"] = Metric(123.45m, MetricUnit.Currency),
            ["AverageHoldingPeriod"] = Metric(12.3333333m, MetricUnit.Bars),
            ["MaxConsecutiveWins"] = Metric(4m, MetricUnit.Count),
            ["Exposure"] = Metric(0.25m, MetricUnit.ExposureRatio),
            ["ExposureAdjustedCAGR"] = Metric(0.08m, MetricUnit.ReturnRatio),
        });

        ImmutableArray<BacktestMetricDisplayRow> rows = WithCulture(cultureName,
            () => Rows(report, NullLocalizationService.Instance));

        Assert.Equal("123.4500", RowOf(rows, "GrossProfit").ValueText);
        Assert.Equal("12.33", RowOf(rows, "AverageHoldingPeriod").ValueText);
        Assert.Equal("4", RowOf(rows, "MaxConsecutiveWins").ValueText);
        Assert.Equal("25.00%", RowOf(rows, "Exposure").ValueText);
        Assert.Equal("8.00%", RowOf(rows, "ExposureAdjustedCAGR").ValueText);
    }

    // ---- T3 -------------------------------------------------------------------------------------

    [Fact]
    public void Build_RightCensoredDrawdownDuration_IsShownAsLowerBound_En() =>
        AssertRightCensoredDrawdownDuration("en", "≥ 30");

    [Fact]
    public void Build_RightCensoredDrawdownDuration_IsShownAsLowerBound_Ja() =>
        // The Japanese template is "{0} or more" (U+4EE5 U+4E0A), built from code points so the source stays English-only.
        AssertRightCensoredDrawdownDuration("ja", "30 " + char.ConvertFromUtf32(0x4EE5) + char.ConvertFromUtf32(0x4E0A));

    private static void AssertRightCensoredDrawdownDuration(string languageCode, string expectedValueText)
    {
        FakeLocalizationService locale = LoadLocale(languageCode);
        BacktestReport report = WithMembers(GenerateReport(10m, -4m), new Dictionary<string, JsonNode?>
        {
            ["MaxDepthDrawdownDuration"] = Metric(30m, MetricUnit.Bars),
            ["MaxDepthDrawdownDurationRightCensored"] = true,
            ["LongestDrawdownDuration"] = Metric(30m, MetricUnit.Bars),
            ["LongestDrawdownDurationRightCensored"] = false,
        });

        ImmutableArray<BacktestMetricDisplayRow> rows = WithCulture("en-US", () => Rows(report, locale));

        BacktestMetricDisplayRow censored = RowOf(rows, "MaxDepthDrawdownDuration");
        Assert.True(censored.IsLowerBound);
        Assert.Equal(expectedValueText, censored.ValueText);
        Assert.Contains(locale.GetString("Backtest_Metric_Tooltip_LowerBound"), censored.TooltipText, StringComparison.Ordinal);

        BacktestMetricDisplayRow recovered = RowOf(rows, "LongestDrawdownDuration");
        Assert.False(recovered.IsLowerBound);
        Assert.Equal("30", recovered.ValueText);
        Assert.DoesNotContain(locale.GetString("Backtest_Metric_Tooltip_LowerBound"), recovered.TooltipText, StringComparison.Ordinal);
    }

    // ---- T4 -------------------------------------------------------------------------------------

    [Fact]
    public void Build_NoTrades_TradeMetricsAreNotAvailableWithStatusAndReasonInTooltip()
    {
        FakeLocalizationService locale = LoadLocale("en");
        BacktestReport report = GenerateReport();

        ImmutableArray<BacktestMetricDisplayRow> rows = Rows(report, locale);

        string[] tradeMetrics = CanonicalNames.Take(10).ToArray();
        foreach (string name in tradeMetrics)
        {
            BacktestMetricDisplayRow row = RowOf(rows, name);
            Assert.NotEqual(MetricStatus.Valid, row.Status);
            Assert.Equal(locale.GetString("Backtest_Metric_NotAvailable"), row.ValueText);
            Assert.Equal(BacktestMetricSemantic.Neutral, row.Semantic);
            Assert.StartsWith(locale.GetString("Backtest_Metric_Tooltip_" + name), row.TooltipText, StringComparison.Ordinal);
        }

        BacktestMetricDisplayRow grossProfit = RowOf(rows, "GrossProfit");
        Assert.Equal(MetricStatus.InsufficientData, grossProfit.Status);
        Assert.EndsWith(
            $"{locale.GetString("Backtest_Metric_Status_InsufficientData")} ({locale.GetString("Backtest_Metric_Reason_NoClosedTrades")})",
            grossProfit.TooltipText,
            StringComparison.Ordinal);
    }

    // ---- T5 -------------------------------------------------------------------------------------

    [Fact]
    public void Build_OnlyWinningTrades_PayoffRatioIsNotAvailableWithPositiveInfinityInTooltip()
    {
        FakeLocalizationService locale = LoadLocale("en");
        BacktestReport report = GenerateReport(10m, 6m);

        BacktestMetricDisplayRow payoff = RowOf(Rows(report, locale), "PayoffRatio");

        Assert.Equal(MetricStatus.PositiveInfinity, payoff.Status);
        Assert.Equal(locale.GetString("Backtest_Metric_NotAvailable"), payoff.ValueText);
        Assert.Equal(BacktestMetricSemantic.Neutral, payoff.Semantic);
        Assert.Contains(locale.GetString("Backtest_Metric_Status_PositiveInfinity"), payoff.TooltipText, StringComparison.Ordinal);
        Assert.Contains(locale.GetString("Backtest_Metric_Reason_ZeroDivisor"), payoff.TooltipText, StringComparison.Ordinal);
    }

    // ---- T9 -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void Locale_ContainsEveryMetricKey(string languageCode)
    {
        string path = Path.Combine(RepoPaths.Root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{languageCode}.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;

        var required = new List<string>
        {
            "Backtest_Metric_NotAvailable",
            "Backtest_Metric_LowerBoundTemplate",
            "Backtest_Metric_Tooltip_LowerBound",
            "Backtest_Metric_Tooltip_NonValid",
        };
        required.AddRange(CanonicalNames.Select(n => "Backtest_Metric_" + n));
        required.AddRange(CanonicalNames.Select(n => "Backtest_Metric_Tooltip_" + n));
        required.AddRange(Enum.GetValues<MetricStatus>().Select(s => "Backtest_Metric_Status_" + s));
        required.AddRange(Enum.GetValues<MetricReason>().Select(r => "Backtest_Metric_Reason_" + r));

        List<string> missing = required
            .Where(key => !root.TryGetProperty(key, out JsonElement value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            .ToList();

        Assert.True(missing.Count == 0, $"{languageCode}.json is missing or has empty values for: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Locale_TemplatesKeepTheirPlaceholders()
    {
        foreach (string languageCode in new[] { "en", "ja" })
        {
            FakeLocalizationService locale = LoadLocale(languageCode);
            Assert.Contains("{0}", locale.GetString("Backtest_Metric_LowerBoundTemplate"), StringComparison.Ordinal);
            string nonValid = locale.GetString("Backtest_Metric_Tooltip_NonValid");
            Assert.Contains("{0}", nonValid, StringComparison.Ordinal);
            Assert.Contains("{1}", nonValid, StringComparison.Ordinal);
        }
    }

}
