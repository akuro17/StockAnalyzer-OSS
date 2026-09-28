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
        var reportA = BacktestTestFactory.CreateStubReport();
        var reportB = BacktestTestFactory.CreateStubReport();
        vm.Update(BacktestTestFactory.CreateResult(), reportA, 0);

        Task export = vm.ExportReportCommand.ExecuteAsync(null);
        vm.Update(BacktestTestFactory.CreateResult(), reportB, 0);
        dialog.FolderResponse.SetResult(System.IO.Path.GetTempPath());
        await export;

        Assert.Same(reportA, exporter.LastReport);
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

        Assert.Same(artifact, exporter.LastEvaluationArtifact);
        Assert.Null(exporter.LastReport);
        Assert.Equal("EVALUATION-JSON v1", dialog.LastFolderTitle);
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
