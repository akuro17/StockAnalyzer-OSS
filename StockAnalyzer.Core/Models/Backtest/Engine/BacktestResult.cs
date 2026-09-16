using System;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace StockAnalyzer.Core.Models.Backtest.Engine;

public sealed class BacktestResult
{
    public ImmutableArray<BacktestOrder> Orders { get; }
    public ImmutableArray<BacktestFill> Fills { get; }
    public ImmutableArray<BacktestTrade> Trades { get; }
    public ImmutableArray<EquityPoint> EquityPoints { get; }
    public ImmutableArray<BacktestSignal> Signals { get; }
    public BacktestConfiguration Configuration { get; }
    public RunStatus Status { get; }
    public string StrategyName { get; }
    public byte[] ReproducibilityHash { get; }

    /// <summary>
    /// True only for the CreateEmpty (zero-bar-input) path — distinguishes "no input data was
    /// available to run against" from "the run completed normally but the strategy produced zero
    /// trades". User-requested disambiguation flag; there is no separate RunStatus member for this
    /// (see the DERIVED CONVENTION remarks on CreateEmpty below).
    /// </summary>
    public bool IsInsufficientData { get; }

    public BacktestResult(
        ImmutableArray<BacktestOrder> orders,
        ImmutableArray<BacktestFill> fills,
        ImmutableArray<BacktestTrade> trades,
        ImmutableArray<EquityPoint> equityPoints,
        ImmutableArray<BacktestSignal> signals,
        BacktestConfiguration configuration,
        RunStatus status,
        string strategyName,
        byte[] reproducibilityHash,
        bool isInsufficientData)
    {
        Orders = orders.IsDefault ? ImmutableArray<BacktestOrder>.Empty : orders;
        Fills = fills.IsDefault ? ImmutableArray<BacktestFill>.Empty : fills;
        Trades = trades.IsDefault ? ImmutableArray<BacktestTrade>.Empty : trades;
        EquityPoints = equityPoints.IsDefault ? ImmutableArray<EquityPoint>.Empty : equityPoints;
        Signals = signals.IsDefault ? ImmutableArray<BacktestSignal>.Empty : signals;
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Status = status;
        StrategyName = strategyName ?? throw new ArgumentNullException(nameof(strategyName));
        ReproducibilityHash = reproducibilityHash ?? throw new ArgumentNullException(nameof(reproducibilityHash));
        IsInsufficientData = isInsufficientData;
    }

    /// <summary>
    /// Count==0 path (source spec section 5.2): "an Empty result that still carries the caller's
    /// BacktestConfiguration". DERIVED CONVENTION: RunStatus has no "InsufficientData" member (the
    /// spec's own section 5.4 enum list is the authoritative full RunStatus set) — mapped to
    /// Completed here since an empty run over zero bars is not itself an error condition. Flagged
    /// for confirmation in Y:\Temp\sa_implementation_plan_BacktestEngineP1.md.
    /// DERIVED CONVENTION: the ReproducibilityHash content/format for a real (non-empty) run is
    /// finalized when BacktestEngine.Run is implemented (task #6); this Empty-path hash is only a
    /// stable placeholder over a fixed marker string, not a hash of any run content.
    /// </summary>
    public static BacktestResult CreateEmpty(BacktestConfiguration config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("StockAnalyzer.Core.Backtest.Engine.BacktestResult.Empty.v1"));

        return new BacktestResult(
            ImmutableArray<BacktestOrder>.Empty,
            ImmutableArray<BacktestFill>.Empty,
            ImmutableArray<BacktestTrade>.Empty,
            ImmutableArray<EquityPoint>.Empty,
            ImmutableArray<BacktestSignal>.Empty,
            config,
            RunStatus.Completed,
            strategyName: string.Empty,
            reproducibilityHash: hash,
            isInsufficientData: true);
    }
}
