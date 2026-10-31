using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// One part of the optional diagnostics failing on arithmetic must not discard the finished backtest or the other parts: the part is left out and named
/// in diagnosticsUnavailable with a reason. A decimal overflow needs a tiny positive initial capital and a huge equity, which no engine run produces, so
/// these tests call the calculation behind the tool directly with crafted points.
/// </summary>
public sealed class BacktestDiagnosticsIsolationTests
{
    private const decimal TinyCapital = 0.01m;
    private const decimal Huge = 70_000_000_000_000_000_000_000_000_000m; // 7e28, below the decimal maximum 7.9e28
    private static readonly DateTime Start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly BacktestDiagnosticsRequest All = new(EquityCurveResolution.Bar, 3, true);

    private static ImmutableArray<EquityPoint> Points(params decimal[] equity) =>
        equity.Select((e, i) => new EquityPoint(i, Start.AddDays(i), e, e, 0m, 0m)).ToImmutableArray();

    private static JsonElement Written(BacktestDiagnostics diagnostics)
    {
        string json = ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            diagnostics.Write(writer);
            writer.WriteEndObject();
        });
        return JsonDocument.Parse(json).RootElement;
    }

    private static string[] Unavailable(JsonElement written) =>
        written.GetProperty(BacktestDiagnostics.UnavailableKey).EnumerateArray()
            .Select(entry => $"{entry.GetProperty("part").GetString()}:{entry.GetProperty("reason").GetString()}")
            .ToArray();

    [Fact]
    public void AnOverflowInOnePartLeavesThatPartOutAndKeepsTheOthers()
    {
        // Equity rises to 7e28: the curve and the (empty) episode list are fine, the return against the 0.01 start overflows.
        BacktestDiagnostics diagnostics = BacktestDiagnostics.Compute(TinyCapital, Points(Huge), Start, Start.AddDays(30), All, new McpLimits());

        JsonElement written = Written(diagnostics);
        Assert.Equal(new[] { "includePeriodicReturns:ArithmeticOverflow" }, Unavailable(written));
        Assert.Equal(1, written.GetProperty("equityCurve").GetProperty("pointCount").GetInt32());
        Assert.Equal(0, written.GetProperty("drawdownEpisodes").GetArrayLength());
        Assert.False(written.TryGetProperty("periodicReturns", out _));
    }

    [Fact]
    public void EveryPartThatOverflowsIsNamedWithTheSharedReasonCode()
    {
        // Equity -7e28 against a 0.01 running high: every ratio overflows.
        BacktestDiagnostics diagnostics = BacktestDiagnostics.Compute(TinyCapital, Points(-Huge), Start, Start.AddDays(30), All, new McpLimits());

        JsonElement written = Written(diagnostics);
        Assert.Equal(
            new[] { "includeEquityCurve:ArithmeticOverflow", "includeDrawdownEpisodes:ArithmeticOverflow", "includePeriodicReturns:ArithmeticOverflow" },
            Unavailable(written));
        Assert.False(written.TryGetProperty("equityCurve", out _));
        Assert.False(written.TryGetProperty("drawdownEpisodes", out _));
        Assert.False(written.TryGetProperty("periodicReturns", out _));
        Assert.Equal("ArithmeticOverflow", BacktestDiagnosticReasons.ArithmeticOverflow);
        Assert.Equal("NonFiniteResult", BacktestDiagnosticReasons.NonFiniteResult);
    }

    [Fact]
    public void WithoutAFailureNoUnavailableKeyIsWritten()
    {
        BacktestDiagnostics diagnostics = BacktestDiagnostics.Compute(100m, Points(90m, 110m), Start, Start.AddDays(30), All, new McpLimits());

        Assert.False(Written(diagnostics).TryGetProperty(BacktestDiagnostics.UnavailableKey, out _));
    }

    [Fact]
    public void ACurveOverTheLimitIsStillLimitExceeded_NotSwallowedAsAnArithmeticFailure()
    {
        var error = Assert.Throws<ToolException>(() =>
            BacktestDiagnostics.Compute(100m, Points(90m, 110m, 120m), Start, Start.AddDays(30), All, new McpLimits { MaxEquityCurvePoints = 2 }));

        Assert.Equal("LIMIT_EXCEEDED", error.Code);
        Assert.Equal("includeEquityCurve", error.Parameter);
    }

    [Fact]
    public void WeeklyBarsRefuseOnlyTheMonthAndYearParts()
    {
        Assert.Equal("UNSUPPORTED_TIMEFRAME", Assert.Throws<ToolException>(() =>
            BacktestDiagnostics.EnsureTimeFrameSupported(new BacktestDiagnosticsRequest(null, 0, true), StockAnalyzer.Core.Models.TimeFrame.W1)).Code);
        Assert.Equal("UNSUPPORTED_TIMEFRAME", Assert.Throws<ToolException>(() =>
            BacktestDiagnostics.EnsureTimeFrameSupported(new BacktestDiagnosticsRequest(EquityCurveResolution.Monthly, 0, false), StockAnalyzer.Core.Models.TimeFrame.W1)).Code);
        BacktestDiagnostics.EnsureTimeFrameSupported(new BacktestDiagnosticsRequest(EquityCurveResolution.Weekly, 5, false), StockAnalyzer.Core.Models.TimeFrame.W1);
        BacktestDiagnostics.EnsureTimeFrameSupported(new BacktestDiagnosticsRequest(EquityCurveResolution.Bar, 5, false), StockAnalyzer.Core.Models.TimeFrame.W1);
        foreach (StockAnalyzer.Core.Models.TimeFrame other in new[] { StockAnalyzer.Core.Models.TimeFrame.D1, StockAnalyzer.Core.Models.TimeFrame.MN1 })
        {
            BacktestDiagnostics.EnsureTimeFrameSupported(All, other);
        }
    }
}
