using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

public sealed record QualifiedBacktestReportResult(
    BacktestReport Report,
    BacktestRunFingerprint RunFingerprint,
    BootstrapDiagnostics BootstrapDiagnostics);

public interface IQualifiedBacktestReportGenerator
{
    QualifiedBacktestReportResult GenerateQualified(
        BacktestResult result,
        BacktestReportOptions options,
        SamplingQualification sampling,
        CancellationToken cancellationToken = default);
}
