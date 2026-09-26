#nullable enable
using System;
using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class YFinanceApproximateModeTests
{
    private static ImmutableArray<CandleData> Bars() =>
        SyntheticBars.FlatSeries(100m, 100m, 100m, 110m, 112m, 90m, 88m, 100m);

    private static BacktestConfiguration Config(ExecutionModel model) => new()
    {
        ExecutionModel = model,
        InitialCapital = 1000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 3m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
    };

    [Fact]
    public void DailyApproximation_ReusesLegacyCalculationsButNotModeIdentity()
    {
        BacktestInput input = VerificationHarness.MakeInput(Bars());
        var strategy = VerificationHarness.SmaTrendStrategy(3);
        BacktestConfiguration legacyConfig = Config(ExecutionModel.Legacy);
        BacktestConfiguration approximateConfig = Config(ExecutionModel.YFinanceApproximate);

        BacktestResult legacy = VerificationHarness.Run(input, legacyConfig, strategy);
        BacktestResult approximate = VerificationHarness.Run(input, approximateConfig, strategy);

        Assert.Equal(legacy.Orders, approximate.Orders);
        Assert.Equal(legacy.Fills, approximate.Fills);
        Assert.Equal(legacy.Trades, approximate.Trades);
        Assert.Equal(legacy.EquityPoints, approximate.EquityPoints);
        Assert.Equal(legacy.Signals, approximate.Signals);
        Assert.Equal(legacy.Status, approximate.Status);
        Assert.Equal(legacy.ReproducibilityHash, approximate.ReproducibilityHash);
        Assert.Equal(ExecutionModel.YFinanceApproximate, approximate.Configuration.ExecutionModel);

        BacktestRunFingerprintBuilder legacyFrozen = BacktestRunFingerprintBuilder.Freeze(input, legacyConfig, strategy);
        BacktestRunFingerprintBuilder approximateFrozen = BacktestRunFingerprintBuilder.Freeze(input, approximateConfig, strategy);
        Assert.NotEqual(legacyFrozen.Seal(legacy), approximateFrozen.Seal(approximate));
        Assert.Throws<ArgumentException>(() => approximateFrozen.Seal(legacy));
    }

    [Fact]
    public void Approximation_RejectsNonDailyInputWithoutChangingStrictGuard()
    {
        ImmutableArray<CandleData> bars = Bars();
        BacktestInput weekly = new(bars, "TEST", TimeFrame.W1, BacktestInput.CurrentDataVersion,
            SyntheticBars.Start, SyntheticBars.Start.AddDays(bars.Length + 1), 0, 0);
        var strategy = VerificationHarness.SmaTrendStrategy(3);

        Assert.Throws<NotSupportedException>(() => VerificationHarness.Run(
            weekly, Config(ExecutionModel.YFinanceApproximate), strategy));
        Assert.Throws<NotSupportedException>(() => VerificationHarness.Run(
            VerificationHarness.MakeInput(bars), Config(ExecutionModel.StrictEvidence), strategy));
    }

    [Fact]
    public void ReportSerialization_LabelsOnlyApproximateModeAndRetainsLegacyMetrics()
    {
        BacktestInput input = VerificationHarness.MakeInput(Bars());
        var strategy = VerificationHarness.SmaTrendStrategy(3);
        BacktestResult legacy = VerificationHarness.Run(input, Config(ExecutionModel.Legacy), strategy);
        BacktestResult approximate = VerificationHarness.Run(input, Config(ExecutionModel.YFinanceApproximate), strategy);
        var options = new BacktestReportOptions(TimeFrame.D1, 0, SyntheticBars.Start,
            SyntheticBars.Start.AddDays(Bars().Length + 1))
        {
            AnnualPeriods = 252,
        };
        var generator = new BacktestReportGenerator();

        BacktestReport legacyReport = generator.Generate(legacy, options);
        BacktestReport approximateReport = generator.Generate(approximate, options);

        BacktestReportValidator.Validate(approximateReport);
        Assert.Null(legacyReport.ExecutionModel);
        Assert.Null(legacyReport.ExecutionDisclosure);
        Assert.Equal(ExecutionModel.YFinanceApproximate, approximateReport.ExecutionModel);
        Assert.Equal(BacktestReport.YFinanceApproximateDisclosure, approximateReport.ExecutionDisclosure);
        Assert.Equal(legacyReport.TotalPnL, approximateReport.TotalPnL);
        Assert.Equal(legacyReport.TotalReturn, approximateReport.TotalReturn);
        Assert.False(JsonSerializer.Serialize(legacyReport).Contains("ExecutionModel", StringComparison.Ordinal));
        Assert.False(JsonSerializer.Serialize(legacyReport).Contains("ExecutionDisclosure", StringComparison.Ordinal));
        Assert.Contains("\"ExecutionModel\":2", JsonSerializer.Serialize(approximateReport), StringComparison.Ordinal);
        Assert.Contains("user-declared, unverified", JsonSerializer.Serialize(approximateReport), StringComparison.Ordinal);
    }
}
