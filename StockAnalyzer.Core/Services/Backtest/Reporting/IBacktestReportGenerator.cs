using StockAnalyzer.Core.Models.Backtest.Engine;
using System.Threading;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>Mirrors the existing <see cref="Engine.IBacktestEngine"/> DI-interface convention.</summary>
public interface IBacktestReportGenerator
{
    BacktestReport Generate(BacktestResult result, BacktestReportOptions options);
}

/// <summary>Additive report-generation capability for callers that can provide cooperative cancellation.</summary>
public interface ICancellableBacktestReportGenerator
{
    BacktestReport Generate(BacktestResult result, BacktestReportOptions options, CancellationToken cancellationToken);
}
