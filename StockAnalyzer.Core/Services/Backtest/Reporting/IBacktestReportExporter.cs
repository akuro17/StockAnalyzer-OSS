using System.Threading.Tasks;
using StockAnalyzer.Core.Services.Backtest.Evaluation;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

public interface IBacktestReportExporter
{
    /// <summary>Writes <paramref name="report"/> as JSON to Data/Backtest/Reports/<paramref name="fileName"/> (created if missing).</summary>
    Task ExportAsync(BacktestReport report, string fileName);

    /// <summary>Writes <paramref name="report"/> as JSON to <paramref name="directoryPath"/>/<paramref name="fileName"/> (directory created if missing) - for a user-chosen export destination instead of the fixed Data/Backtest/Reports/ location.</summary>
    Task ExportAsync(BacktestReport report, string directoryPath, string fileName);

    /// <summary>Writes the explicit versioned evaluation-evidence wrapper without changing report-only export behavior.</summary>
    Task ExportEvaluationAsync(BacktestEvaluationArtifact artifact, string fileName) =>
        throw new System.NotSupportedException("This exporter does not support evaluation artifacts.");

    /// <summary>Writes the explicit versioned evaluation-evidence wrapper to a user-selected directory.</summary>
    Task ExportEvaluationAsync(BacktestEvaluationArtifact artifact, string directoryPath, string fileName) =>
        throw new System.NotSupportedException("This exporter does not support evaluation artifacts.");
}
