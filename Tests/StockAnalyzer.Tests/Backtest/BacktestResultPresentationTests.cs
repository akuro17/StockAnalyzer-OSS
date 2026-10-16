using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

public class BacktestResultPresentationTests
{
    [Fact]
    public void PercentageFormatter_HandlesDecimalExtremesWithoutOverflow()
    {
        Assert.Equal(
            "7922816251426433759354395033500.00%",
            BacktestMetricFormatter.FormatPercentage(decimal.MaxValue, CultureInfo.InvariantCulture));
        Assert.Equal(
            "-7922816251426433759354395033500.00%",
            BacktestMetricFormatter.FormatPercentage(decimal.MinValue, CultureInfo.InvariantCulture));
        Assert.Equal("0.00%", BacktestMetricFormatter.FormatPercentage(-0.0000001m, CultureInfo.InvariantCulture));
        Assert.Equal("12.34%", BacktestMetricFormatter.FormatPercentage(0.12345m, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TryUpdate_InvalidCandidate_PreservesPublishedPresentation()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            new FakeBacktestReportExporter(),
            new FakeDialogService());
        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(), 0);
        BacktestResultPresentation published = vm.Presentation!;

        bool updated = vm.TryUpdate(
            BacktestTestFactory.CreateResult(),
            new StockAnalyzer.Core.Services.Backtest.Reporting.BacktestReport(),
            0,
            System.Collections.Immutable.ImmutableArray<StockAnalyzer.Core.Models.Backtest.Engine.BacktestConditionEntry>.Empty,
            false,
            out Exception? failure);

        Assert.False(updated);
        Assert.NotNull(failure);
        Assert.Same(published, vm.Presentation);
    }

    [Fact]
    public async Task Export_CapturesReportBeforeFolderPickerCompletes()
    {
        var exporter = new FakeBacktestReportExporter();
        var dialog = new FakeDialogService { FolderResponse = new TaskCompletionSource<string?>() };
        var vm = new BacktestResultsViewModel(NullLocalizationService.Instance, exporter, dialog);
        var tradesA = ImmutableArray.Create(new BacktestTrade(1, TradeSide.Long, 0, DateTime.UtcNow, 100m, 1, DateTime.UtcNow, 110m, 1m, 10m, 10m, 0m, 0m, 1, false));
        var tradesB = ImmutableArray.Create(new BacktestTrade(2, TradeSide.Short, 0, DateTime.UtcNow, 200m, 1, DateTime.UtcNow, 190m, 1m, 10m, 10m, 0m, 0m, 1, false));
        vm.Update(BacktestTestFactory.CreateResult(trades: tradesA), BacktestTestFactory.CreateStubReport(), 0);

        Task export = vm.ExportReportCommand.ExecuteAsync(null);
        vm.Update(BacktestTestFactory.CreateResult(trades: tradesB), BacktestTestFactory.CreateStubReport(), 0);
        dialog.FolderResponse.SetResult(System.IO.Path.GetTempPath());
        await export;

        Assert.Equal(tradesA, exporter.LastTrades);
    }

    [Fact]
    public async Task Export_UsesSaveFileDialogAndExportsToUserSpecifiedFileName()
    {
        var exporter = new FakeBacktestReportExporter();
        var dialog = new FakeDialogService
        {
            FileToReturn = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "custom_strategy_trades.csv")
        };
        var localization = new FakeLocalizationService(new System.Collections.Generic.Dictionary<string, string>
        {
            ["Backtest_Export_SaveFileTitle"] = "Save Trades As",
            ["Backtest_Export_Success"] = "Saved: {0}",
        });
        var vm = new BacktestResultsViewModel(localization, exporter, dialog);
        var trades = ImmutableArray.Create(new BacktestTrade(10, TradeSide.Long, 0, DateTime.UtcNow, 150m, 1, DateTime.UtcNow, 160m, 1m, 10m, 10m, 0m, 0m, 1, false));
        vm.Update(BacktestTestFactory.CreateResult(trades: trades), BacktestTestFactory.CreateStubReport(), 0);

        await vm.ExportReportCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.SaveFileDialogCallCount);
        Assert.Equal("Save Trades As", dialog.LastSaveTitle);
        Assert.Equal("csv", dialog.LastSaveDefaultExtension);
        Assert.NotNull(dialog.LastSaveDefaultFilename);
        Assert.StartsWith("backtest_trades_", dialog.LastSaveDefaultFilename);
        Assert.EndsWith(".csv", dialog.LastSaveDefaultFilename);
        Assert.Equal(new[] { "csv" }, dialog.LastSaveFilters);

        Assert.Equal(trades, exporter.LastTrades);
        Assert.Equal("custom_strategy_trades.csv", exporter.LastFileName);
        Assert.Equal("Saved: custom_strategy_trades.csv", vm.ExportStatusMessage);
    }

    [Fact]
    public async Task Export_CancelledDialog_DoesNotExport()
    {
        var exporter = new FakeBacktestReportExporter();
        var dialog = new FakeDialogService { FileToReturn = null };
        var vm = new BacktestResultsViewModel(NullLocalizationService.Instance, exporter, dialog);
        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(), 0);

        await vm.ExportReportCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.SaveFileDialogCallCount);
        Assert.Equal(0, exporter.ExportCallCount);
        Assert.Null(vm.ExportStatusMessage);
    }

    [Fact]
    public void Update_RiskDescriptionUsesFrozenRunSettingsAndPreservesPrecision()
    {
        var vm = new BacktestResultsViewModel(
            new FakeLocalizationService(new System.Collections.Generic.Dictionary<string, string>
            {
                ["Backtest_Results_RiskStopLoss"] = "Stop-Loss: {0}",
                ["Backtest_Results_RiskTakeProfit"] = "Take-Profit: {0}",
                ["Backtest_Results_RiskDisabled"] = "Disabled",
                ["Backtest_Results_RiskBreakeven"] = "{0} (breakeven)",
            }),
            new FakeBacktestReportExporter(),
            new FakeDialogService());
        var risk = new BacktestRiskManagementSettings
        {
            StopLossPercent = 0m,
            TakeProfitPercent = 0.1234567890123456789012345678m,
        };

        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(), 0,
            System.Collections.Immutable.ImmutableArray<BacktestConditionEntry>.Empty, false, risk);

        Assert.Contains("0%", vm.Presentation!.RiskManagementDescription);
        Assert.Contains("12.34567890123456789012345678%", vm.Presentation.RiskManagementDescription);

        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(), 0);
        Assert.Contains("Stop-Loss: Disabled", vm.Presentation!.RiskManagementDescription);
        Assert.Contains("Take-Profit: Disabled", vm.Presentation.RiskManagementDescription);
    }

    [Fact]
    public async Task EvaluationArtifact_DrivesAuditPresentationAndArtifactExportWithoutSubstitution()
    {
        DateTime timestamp = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        BacktestResult result = BacktestTestFactory.CreateResult();
        var input = new BacktestInput(
            ImmutableArray<CandleData>.Empty,
            "TEST",
            TimeFrame.D1,
            BacktestInput.CurrentDataVersion,
            timestamp,
            timestamp,
            0,
            0);
        var service = new BacktestEvaluationService(
            new FixedResultEngine(result),
            new BacktestReportGenerator(),
            new NoSamplingEvidenceTrustPolicy());
        BacktestEvaluationArtifact artifact = service.Evaluate(
            input,
            result.Configuration,
            BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()),
            new BacktestReportOptions(TimeFrame.D1, 0, timestamp, timestamp) { AnnualPeriods = 252 });

        var exporter = new FakeBacktestReportExporter();
        var dialog = new FakeDialogService { FolderToReturn = System.IO.Path.GetTempPath() };
        var localization = new MapLocalizationService(new()
        {
            ["Backtest_Audit_SamplingStatus_Unverified"] = "UNVERIFIED-LOCALIZED",
            ["Backtest_Audit_SamplingReason_NoEvidence"] = "NO-EVIDENCE-LOCALIZED",
            ["Backtest_Audit_DrawdownStatus_Available"] = "DD-AVAILABLE-LOCALIZED",
            ["Backtest_Audit_DrawdownStatus_NoDrawdown"] = "DD-NONE-LOCALIZED",
            ["Backtest_Audit_Reason_NoDrawdown"] = "DD-REASON-LOCALIZED",
            ["Backtest_Audit_RiskMode_0"] = "LEGACY-MODE-LOCALIZED",
            ["Backtest_Audit_SamplingTemplate"] = "{0} / {1}",
            ["Backtest_Audit_DrawdownStateTemplate"] = "{0} / {1}",
            ["Backtest_Audit_AnnualizationTemplate"] = "A={0}; sampling={1}",
            ["Backtest_Export_SelectFolderTitle_Evaluation"] = "EVALUATION-JSON v{0}",
        });
        var vm = new BacktestResultsViewModel(localization, exporter, dialog);
        vm.Update(artifact, 0, ImmutableArray<BacktestConditionEntry>.Empty, true, null);

        Assert.Same(artifact, vm.EvaluationArtifact);
        Assert.True(vm.HasAudit);
        Assert.Contains(vm.AuditRows, row => row.ValueText == artifact.RunFingerprint.Sha256);
        Assert.Contains(vm.AuditRows, row => row.ValueText == artifact.ReportIdentity.Sha256);
        Assert.Contains(vm.AuditRows, row => row.LabelKey == "Backtest_Audit_Sampling" && row.ValueText == "UNVERIFIED-LOCALIZED / NO-EVIDENCE-LOCALIZED");
        Assert.Contains(vm.AuditRows, row => row.LabelKey == "Backtest_Audit_RiskMode" && row.ValueText == "LEGACY-MODE-LOCALIZED");
        Assert.Contains(vm.AuditRows, row => row.LabelKey == "Backtest_Audit_Annualization" && row.ValueText == "A=252; sampling=UNVERIFIED-LOCALIZED");
        Assert.Contains(vm.AuditRows, row => row.LabelKey == "Backtest_Audit_RatioDrawdown" &&
            (row.ValueText.StartsWith("DD-AVAILABLE-LOCALIZED", StringComparison.Ordinal) || row.ValueText.StartsWith("DD-NONE-LOCALIZED", StringComparison.Ordinal)));
        Assert.DoesNotContain(vm.AuditRows, row => row.ValueText.Contains("seed=", StringComparison.Ordinal) ||
            row.ValueText.Contains("unrecovered", StringComparison.Ordinal) || row.ValueText.Contains("peak=", StringComparison.Ordinal));

        await vm.ExportReportCommand.ExecuteAsync(null);

        Assert.Equal(result.Trades, exporter.LastTrades);
    }

    [Theory]
    [InlineData(0, "2 of 3", "1 of 3")]
    [InlineData(1, "2 of 2", "1 of 2")]
    [InlineData(2, "1 of 1", "1 of 1")]
    public void AuditRows_ShowDetectedAndAffectingOrderDependentBarsOfTheEvaluatedWindow(int evaluationStartIndex, string detected, string affecting)
    {
        // Bars 1 (detected only) and 2 (detected and affecting) are order-dependent; the split warning at bar 0 is a different code and never counts.
        BacktestResult result = WithWarnings(
            BacktestTestFactory.CreateResult(equityPoints: ThreeBars()),
            new BacktestWarning(WarningCode.PotentialUnadjustedSplit, 0, BarTime(0), "split"),
            new BacktestWarning(WarningCode.AmbiguousIntrabarOrder, 1, BarTime(1), "first"),
            new BacktestWarning(WarningCode.AmbiguousIntrabarOrderAffectsOutcome, 2, BarTime(2), "second"));

        BacktestResultsViewModel vm = UpdateWith(result, evaluationStartIndex);

        Assert.Contains(vm.AuditRows, row => row.LabelKey == "Backtest_Audit_OrderDependentBars" && row.ValueText == detected);
        Assert.Contains(vm.AuditRows, row => row.LabelKey == "Backtest_Audit_OrderDependentBarsAffecting" && row.ValueText == affecting);
    }

    [Fact]
    public void AuditRows_ShowZeroAsEvidenceWhenNoBarWasOrderDependent()
    {
        BacktestResultsViewModel vm = UpdateWith(BacktestTestFactory.CreateResult(equityPoints: ThreeBars()), 0);

        Assert.Contains(vm.AuditRows, row => row.LabelKey == "Backtest_Audit_OrderDependentBars" && row.ValueText == "0 of 3");
        Assert.Contains(vm.AuditRows, row => row.LabelKey == "Backtest_Audit_OrderDependentBarsAffecting" && row.ValueText == "0 of 3");
    }

    [Fact]
    public void OrderDependentWarnings_AreShownAsCountsNotAsWarningText()
    {
        BacktestResultsViewModel onlyOrderDependent = UpdateWith(
            WithWarnings(
                BacktestTestFactory.CreateResult(equityPoints: ThreeBars()),
                new BacktestWarning(WarningCode.AmbiguousIntrabarOrder, 1, BarTime(1), "ambiguity one"),
                new BacktestWarning(WarningCode.AmbiguousIntrabarOrderAffectsOutcome, 2, BarTime(2), "ambiguity two")),
            0);
        Assert.False(onlyOrderDependent.HasWarnings);
        Assert.Null(onlyOrderDependent.WarningSummaryText);

        BacktestResultsViewModel mixed = UpdateWith(
            WithWarnings(
                BacktestTestFactory.CreateResult(equityPoints: ThreeBars()),
                new BacktestWarning(WarningCode.PotentialUnadjustedSplit, 0, BarTime(0), "split suspected"),
                new BacktestWarning(WarningCode.AmbiguousIntrabarOrder, 1, BarTime(1), "ambiguity one")),
            0);
        Assert.True(mixed.HasWarnings);
        Assert.Equal("split suspected", mixed.WarningSummaryText);
    }

    private static DateTime BarTime(int index) => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index);

    private static ImmutableArray<EquityPoint> ThreeBars()
        => ImmutableArray.Create(
            new EquityPoint(0, BarTime(0), 1_000_000m, 1_000_000m, 0m, 0m),
            new EquityPoint(1, BarTime(1), 1_000_000m, 1_000_000m, 0m, 0m),
            new EquityPoint(2, BarTime(2), 1_000_000m, 1_000_000m, 0m, 0m));

    private static BacktestResult WithWarnings(BacktestResult result, params BacktestWarning[] warnings)
        => new(
            result.Orders, result.Fills, result.Trades, result.EquityPoints, result.Signals, result.Configuration, result.Status,
            result.StrategyName, result.ReproducibilityHash, result.IsInsufficientData, warnings: warnings.ToImmutableArray());

    private static BacktestResultsViewModel UpdateWith(BacktestResult result, int evaluationStartIndex)
    {
        DateTime timestamp = BarTime(0);
        var input = new BacktestInput(ImmutableArray<CandleData>.Empty, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, timestamp, timestamp, 0, 0);
        BacktestEvaluationArtifact artifact = new BacktestEvaluationService(
                new FixedResultEngine(result), new BacktestReportGenerator(), new NoSamplingEvidenceTrustPolicy())
            .Evaluate(
                input,
                result.Configuration,
                BacktestStrategySpecification.NoOp(Array.Empty<StrategyIndicatorRequest>()),
                new BacktestReportOptions(TimeFrame.D1, 0, timestamp, timestamp) { AnnualPeriods = 252 });
        var localization = new MapLocalizationService(new()
        {
            ["Backtest_Audit_OrderDependentBarsTemplate"] = "{0} of {1}",
        });
        var vm = new BacktestResultsViewModel(localization, new FakeBacktestReportExporter(), new FakeDialogService());
        vm.Update(artifact, evaluationStartIndex, ImmutableArray<BacktestConditionEntry>.Empty, true, null);
        return vm;
    }

    [Fact]
    public void ReportOnlyPresentation_HasNoAuditCard()
    {
        var vm = new BacktestResultsViewModel(NullLocalizationService.Instance, new FakeBacktestReportExporter(), new FakeDialogService());
        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(), 0);

        Assert.True(vm.HasResult);
        Assert.False(vm.HasAudit);
        Assert.Empty(vm.AuditRows);
    }

    private sealed class MapLocalizationService(System.Collections.Generic.Dictionary<string, string> map) : ILocalizationService
    {
        public string GetString(string key) => map.TryGetValue(key, out string? value) ? value : key;
    }

    private sealed class FixedResultEngine(BacktestResult result) : IBacktestEngine
    {
        public BacktestResult Run(
            BacktestInput input,
            BacktestConfiguration configuration,
            IBacktestStrategy strategy,
            CancellationToken cancellationToken = default) => result;
    }
}
