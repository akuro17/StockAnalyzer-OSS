using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Avalonia.Services;

public sealed record BacktestRunSnapshot(
    string Symbol,
    TimeFrame Frame,
    DateTime EvaluationStartUtc,
    DateTime EvaluationEndUtc,
    BacktestConfiguration Configuration,
    ImmutableArray<BacktestConditionEntry> Conditions,
    BacktestRiskManagementSettings? RiskManagement,
    ImmutableArray<StrategyIndicatorRequest> SelectedIndicators,
    BacktestReportDefaults ReportDefaults,
    int BootstrapSeed,
    int BootstrapIterations);

public sealed record PreparedBacktestRun(
    BacktestInput Input,
    BacktestConfiguration Configuration,
    IBacktestStrategy Strategy,
    BacktestReportOptions ReportOptions,
    int EvaluationStartIndex,
    BacktestStrategySpecification StrategySpecification);

public interface IBacktestRunPreparationService
{
    Task<PreparedBacktestRun> PrepareAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class StrictEvidenceAdapterUnavailableException : NotSupportedException
{
    public StrictEvidenceAdapterUnavailableException()
        : base("The current OHLC market-data source does not provide a conforming StrictEvidence adapter.")
    {
    }
}

public sealed class YFinanceApproximateSourceUnavailableException : NotSupportedException
{
    public YFinanceApproximateSourceUnavailableException()
        : base("YFinanceApproximate requires only D1 data from the configured Data/Daily source.")
    {
    }
}

/// <summary>Loads and validates every bar series using one owned button-time snapshot.</summary>
public sealed class BacktestRunPreparationService : IBacktestRunPreparationService
{
    private readonly IDataService _dataService;
    private readonly MarketDataSettings _marketDataSettings;

    public BacktestRunPreparationService(IDataService dataService, IOptions<MarketDataSettings>? marketDataSettings = null)
    {
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _marketDataSettings = marketDataSettings?.Value ?? new MarketDataSettings();
    }

    public async Task<PreparedBacktestRun> PrepareAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Configuration.ExecutionModel == ExecutionModel.StrictEvidence)
        {
            throw new StrictEvidenceAdapterUnavailableException();
        }
        if (snapshot.Configuration.ExecutionModel == ExecutionModel.YFinanceApproximate)
        {
            EnsureYFinanceDailySource(snapshot.Frame);
        }
        if (snapshot.EvaluationStartUtc > snapshot.EvaluationEndUtc)
        {
            throw new ArgumentException("EvaluationStartUtc must be less than or equal to EvaluationEndUtc.", nameof(snapshot));
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CandleData> raw = await _dataService
            .LoadCandlesAsync(snapshot.Symbol, snapshot.Frame, count: 0)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        ImmutableArray<CandleData> rawBars = ImmutableArray.CreateRange(raw);
        _ = new BacktestInput(
            rawBars,
            snapshot.Symbol,
            snapshot.Frame,
            BacktestInput.CurrentDataVersion,
            snapshot.EvaluationStartUtc,
            snapshot.EvaluationEndUtc,
            0,
            0);

        (ImmutableArray<CandleData> bars, int evaluationStartIndex) = BuildEvaluationWindow(
            rawBars,
            snapshot.EvaluationStartUtc,
            snapshot.EvaluationEndUtc);

        IBacktestStrategy strategy;
        BacktestStrategySpecification strategySpecification;
        IReadOnlyList<StrategyIndicatorRequest> activeRequests;
        if (snapshot.Conditions.Length > 0)
        {
            strategy = new ConditionBasedBacktestStrategy(snapshot.Conditions, snapshot.RiskManagement);
            strategySpecification = BacktestStrategySpecification.ConditionBased(snapshot.Conditions, snapshot.RiskManagement);
            activeRequests = strategy.GetRequiredIndicators();
        }
        else
        {
            var ownedRequests = snapshot.SelectedIndicators
                .Select(request => request with { Parameters = request.Parameters?.Clone() })
                .ToImmutableArray();
            strategy = new NoOpBacktestStrategy(ownedRequests);
            strategySpecification = BacktestStrategySpecification.NoOp(ownedRequests);
            activeRequests = ownedRequests;
        }

        HashSet<TimeFrame> foreignFrames = activeRequests
            .Where(request => request.Frame is { } frame && frame != snapshot.Frame)
            .Select(request => request.Frame!.Value)
            .ToHashSet();

        if (snapshot.Configuration.ExecutionModel == ExecutionModel.YFinanceApproximate && foreignFrames.Count > 0)
        {
            throw new YFinanceApproximateSourceUnavailableException();
        }

        Dictionary<TimeFrame, ImmutableArray<CandleData>>? additionalBars = null;
        if (foreignFrames.Count > 0)
        {
            additionalBars = new Dictionary<TimeFrame, ImmutableArray<CandleData>>(foreignFrames.Count);
            foreach (TimeFrame frame in foreignFrames.OrderBy(value => value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<CandleData> foreign = await _dataService
                    .LoadCandlesAsync(snapshot.Symbol, frame, count: 0)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                additionalBars.Add(frame, ImmutableArray.CreateRange(foreign));
            }
        }

        var input = new BacktestInput(
            bars,
            snapshot.Symbol,
            snapshot.Frame,
            BacktestInput.CurrentDataVersion,
            snapshot.EvaluationStartUtc,
            snapshot.EvaluationEndUtc,
            evaluationStartIndex,
            evaluationStartIndex,
            additionalBars);

        int? builtInPeriods = BacktestReportDefaults.BuiltIn.AnnualPeriodsByFrame.TryGetValue(snapshot.Frame, out int builtInValue)
            ? builtInValue
            : null;
        int annualPeriods = snapshot.ReportDefaults.AnnualPeriodsByFrame.TryGetValue(snapshot.Frame, out int configuredValue)
            ? configuredValue
            : builtInPeriods ?? throw new InvalidOperationException($"No AnnualPeriods default is registered for Frame '{snapshot.Frame}'.");

        var reportOptions = new BacktestReportOptions(
            snapshot.Frame,
            evaluationStartIndex,
            snapshot.EvaluationStartUtc,
            snapshot.EvaluationEndUtc)
        {
            AnnualPeriods = annualPeriods,
            AnnualRiskFreeRate = snapshot.ReportDefaults.AnnualRiskFreeRate,
            AnnualMAR = snapshot.ReportDefaults.AnnualMAR,
            BootstrapSeed = snapshot.BootstrapSeed,
            BootstrapIterations = snapshot.BootstrapIterations,
            RunFingerprintBuilder = BacktestRunFingerprintBuilder.Freeze(input, snapshot.Configuration, strategy),
        };

        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedBacktestRun(input, snapshot.Configuration, strategy, reportOptions, evaluationStartIndex, strategySpecification);
    }

    private void EnsureYFinanceDailySource(TimeFrame frame)
    {
        if (frame != TimeFrame.D1)
        {
            throw new YFinanceApproximateSourceUnavailableException();
        }

        string? configuredPath = _marketDataSettings.DailyDataPath;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!Path.IsPathRooted(configuredPath) &&
            !string.Equals(configuredPath?.Replace('\\', '/').TrimEnd('/'),
                MarketDataSettings.DefaultDailyDataPath, comparison))
        {
            throw new YFinanceApproximateSourceUnavailableException();
        }

        string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultDailyDataPath, "*.parquet")));
        string configured = Path.IsPathRooted(configuredPath)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredPath!))
            : expected;
        if (!string.Equals(expected, configured, comparison))
        {
            throw new YFinanceApproximateSourceUnavailableException();
        }
    }

    private static (ImmutableArray<CandleData> Bars, int EvaluationStartIndex) BuildEvaluationWindow(
        ImmutableArray<CandleData> allBars,
        DateTime evaluationStartUtc,
        DateTime evaluationEndUtc)
    {
        int endExclusive = UpperBound(allBars, evaluationEndUtc);
        ImmutableArray<CandleData> trimmed = allBars[..endExclusive];
        return (trimmed, LowerBound(trimmed, evaluationStartUtc));
    }

    private static int LowerBound(IReadOnlyList<CandleData> bars, DateTime threshold)
    {
        int lo = 0;
        int hi = bars.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (bars[mid].Timestamp < threshold) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int UpperBound(IReadOnlyList<CandleData> bars, DateTime threshold)
    {
        int lo = 0;
        int hi = bars.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (bars[mid].Timestamp <= threshold) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
