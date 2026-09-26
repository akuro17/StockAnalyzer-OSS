using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

public class BacktestReportExporterTests
{
    [Fact]
    public async Task ExportAsync_ThenLoad_RoundTripsGeneratedReport()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 110m, 100m, 115m });
        var options = ReportTestHelpers.Options();
        BacktestReport report = new BacktestReportGenerator().Generate(result, options);

        string fileName = "test_export_" + Guid.NewGuid().ToString("N") + ".json";
        string resolvedPath = PathDiscovery.ResolveBacktestReportExportPath(fileName);
        var exporter = new BacktestReportExporter();

        try
        {
            await exporter.ExportAsync(report, fileName);

            Assert.True(File.Exists(resolvedPath));

            BacktestReport? loaded = await AtomicJsonFile.LoadAsync<BacktestReport?>(resolvedPath);
            Assert.NotNull(loaded);
            Assert.Equal(report.TotalPnL.Value, loaded!.TotalPnL.Value);
            Assert.Equal(report.TotalPnL.Status, loaded.TotalPnL.Status);
            Assert.Equal(report.WinRate.Status, loaded.WinRate.Status);
            Assert.Equal(report.SQN.Status, loaded.SQN.Status);
            Assert.Equal(report.SQN.Reason, loaded.SQN.Reason);
            Assert.Equal(report.FormulaVersion, loaded.FormulaVersion);
            Assert.Equal(report.Frame, loaded.Frame);
            Assert.Equal(report.AnnualPeriods, loaded.AnnualPeriods);
            // m=3 here is below the bootstrap's own m>=20 floor, so this also proves a null
            // ConfidenceInterval? round-trips correctly (not silently defaulted by JSON deserialization).
            Assert.Equal(report.AnnualizedSortinoAutocorrelationAdjusted.Status, loaded.AnnualizedSortinoAutocorrelationAdjusted.Status);
            Assert.Equal(report.AnnualizedSortinoAutocorrelationAdjusted.Reason, loaded.AnnualizedSortinoAutocorrelationAdjusted.Reason);
            Assert.Equal(report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval, loaded.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval);
        }
        finally
        {
            if (File.Exists(resolvedPath)) File.Delete(resolvedPath);
        }
    }

    [Fact]
    public async Task ExportAsync_NullReport_Throws()
    {
        var exporter = new BacktestReportExporter();
        await Assert.ThrowsAsync<ArgumentNullException>(() => exporter.ExportAsync(null!, "x.json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExportAsync_BlankFileName_Throws(string fileName)
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 100m });
        var report = new BacktestReportGenerator().Generate(result, ReportTestHelpers.Options());
        var exporter = new BacktestReportExporter();

        await Assert.ThrowsAsync<ArgumentException>(() => exporter.ExportAsync(report, fileName));
    }

    /// <summary>SAで改善 (Y:\Temp\sa_improvement_plan_BacktestResultsUiPolish.md Task 1): the user-chosen-
    /// folder overload writes verbatim to that folder - never through <see cref="PathDiscovery"/>'s fixed
    /// Data/Backtest/Reports/ resolution - and creates the folder if it does not exist yet.</summary>
    [Fact]
    public async Task ExportAsync_ToExplicitDirectory_WritesUnderThatDirectory_CreatingItIfMissing()
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 110m, 100m, 115m });
        BacktestReport report = new BacktestReportGenerator().Generate(result, ReportTestHelpers.Options());

        string directoryPath = Path.Combine(Path.GetTempPath(), "sa_backtest_export_test_" + Guid.NewGuid().ToString("N"));
        string fileName = "test_export_" + Guid.NewGuid().ToString("N") + ".json";
        string expectedPath = Path.Combine(directoryPath, fileName);
        var exporter = new BacktestReportExporter();

        try
        {
            Assert.False(Directory.Exists(directoryPath));

            await exporter.ExportAsync(report, directoryPath, fileName);

            Assert.True(File.Exists(expectedPath));
            BacktestReport? loaded = await AtomicJsonFile.LoadAsync<BacktestReport?>(expectedPath);
            Assert.NotNull(loaded);
            Assert.Equal(report.TotalPnL.Value, loaded!.TotalPnL.Value);
        }
        finally
        {
            if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_ToExplicitDirectory_NullReport_Throws()
    {
        var exporter = new BacktestReportExporter();
        await Assert.ThrowsAsync<ArgumentNullException>(() => exporter.ExportAsync(null!, Path.GetTempPath(), "x.json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExportAsync_ToExplicitDirectory_BlankDirectoryPath_Throws(string directoryPath)
    {
        var result = ReportTestHelpers.BuildResult(100m, new[] { 100m });
        var report = new BacktestReportGenerator().Generate(result, ReportTestHelpers.Options());
        var exporter = new BacktestReportExporter();

        await Assert.ThrowsAsync<ArgumentException>(() => exporter.ExportAsync(report, directoryPath, "x.json"));
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("..\\escape.json")]
    [InlineData("C:\\escape.json")]
    [InlineData("name:stream.json")]
    [InlineData("NUL.json")]
    [InlineData("COM1.data.json")]
    [InlineData("report.json.")]
    [InlineData("report.json ")]
    public async Task ExportAsync_InvalidLeafName_PerformsNoDirectoryCreation(string fileName)
    {
        BacktestReport report = new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, new[] { 101m }),
            ReportTestHelpers.Options());
        string directoryPath = Path.Combine(Path.GetTempPath(), "sa_invalid_leaf_" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new BacktestReportExporter().ExportAsync(report, directoryPath, fileName));

        Assert.False(Directory.Exists(directoryPath));
    }

    [Fact]
    public async Task ExportAsync_InvalidReport_PerformsNoDirectoryCreation()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "sa_invalid_report_" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new BacktestReportExporter().ExportAsync(new BacktestReport(), directoryPath, "report.json"));

        Assert.False(Directory.Exists(directoryPath));
    }

    [Fact]
    public async Task ExportAsync_ConcurrentSameName_SerializesValidJson()
    {
        BacktestReport report = new BacktestReportGenerator().Generate(
            ReportTestHelpers.BuildResult(100m, new[] { 110m, 105m }),
            ReportTestHelpers.Options());
        string directoryPath = Path.Combine(Path.GetTempPath(), "sa_concurrent_export_" + Guid.NewGuid().ToString("N"));
        var exporter = new BacktestReportExporter();

        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => exporter.ExportAsync(report, directoryPath, "same.json")));
            BacktestReport? loaded = await AtomicJsonFile.LoadAsync<BacktestReport?>(Path.Combine(directoryPath, "same.json"));
            Assert.NotNull(loaded);
            BacktestReportValidator.Validate(loaded!);
        }
        finally
        {
            if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath, recursive: true);
        }
    }
}
