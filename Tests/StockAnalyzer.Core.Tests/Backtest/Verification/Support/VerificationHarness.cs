#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// Shared construction helpers for the L1 verification suite. Uses the REAL <see cref="IndicatorFactory"/> and real
/// <see cref="CandleData"/> - nothing about candles or indicators is stubbed.
/// </summary>
internal static class VerificationHarness
{
    public static readonly TimeInForce Gtc = new(true, -1);

    public static BacktestInput MakeInput(ImmutableArray<CandleData> bars, int tradingStartIndex = 0)
        => new(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            SyntheticBars.Start, SyntheticBars.Start.AddDays(bars.Length + 1), 0, tradingStartIndex);

    public static BacktestConfiguration MakeConfig(
        decimal initialCapital = 1_000_000m, decimal commissionFlat = 0m, decimal commissionPerUnit = 0m,
        decimal slippageRatio = 0m, PositionSizingModel sizingModel = PositionSizingModel.FixedQuantity,
        decimal sizingParameter = 100m, decimal initialMarginRatio = 1m, decimal maintenanceMarginRatio = 0.5m,
        decimal liquidationPenaltyRatio = 0m)
        => new()
        {
            InitialCapital = initialCapital,
            CommissionFlat = commissionFlat,
            CommissionPerUnit = commissionPerUnit,
            SlippageRatio = slippageRatio,
            SizingModel = sizingModel,
            SizingParameter = sizingParameter,
            InitialMarginRatio = initialMarginRatio,
            MaintenanceMarginRatio = maintenanceMarginRatio,
            LiquidationPenaltyRatio = liquidationPenaltyRatio,
        };

    public static BacktestEngine CreateEngine() => new(new IndicatorFactory());

    public static BacktestResult Run(BacktestInput input, BacktestConfiguration config, IBacktestStrategy strategy)
        => CreateEngine().Run(input, config, strategy);

    public static StrategyOrderRequest Req(SignalType type, OrderType orderType = OrderType.Market,
        decimal? limitPrice = null, decimal? stopPrice = null, TimeInForce? tif = null, string reason = "r")
        => new(type, orderType, limitPrice, stopPrice, tif ?? Gtc, reason);

    /// <summary>
    /// "Close greater than SMA(period)" entry (Long) and "Close less than SMA(period)" exit, built on the real
    /// <see cref="ConditionBasedBacktestStrategy"/> and real SMA / Price indicators.
    /// </summary>
    public static ConditionBasedBacktestStrategy SmaTrendStrategy(int period)
    {
        BacktestConditionSide Close() => new() { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close };
        BacktestConditionSide Sma() => new() { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = period } };

        var entry = new BacktestConditionEntry
        {
            Left = Close(),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            Right = Sma(),
            Role = BacktestConditionRole.EntryOnly,
            Position = TradeSide.Long,
        };
        var exit = new BacktestConditionEntry
        {
            Left = Close(),
            Operator = ComparisonOperator.LessThan,
            TargetMode = RightHandTargetMode.Indicator,
            Right = Sma(),
            Role = BacktestConditionRole.ExitOnly,
        };
        return new ConditionBasedBacktestStrategy(new[] { entry, exit });
    }
}

/// <summary>
/// Scripts <see cref="IBacktestStrategy.Evaluate"/> and, independently, <see cref="IBacktestStrategy.EvaluateExit"/> per
/// bar index, so a test can submit an Entry-type and an Exit-type request on the very same bar (the reversal shape).
/// The single shared implementation for the whole Verification folder.
/// </summary>
internal sealed class ScriptedStrategy : IBacktestStrategy
{
    private readonly IReadOnlyDictionary<int, StrategyOrderRequest> _primary;
    private readonly IReadOnlyDictionary<int, StrategyOrderRequest> _accompanyingExit;

    public ScriptedStrategy(
        IReadOnlyDictionary<int, StrategyOrderRequest> primary,
        IReadOnlyDictionary<int, StrategyOrderRequest>? accompanyingExit = null)
    {
        _primary = primary;
        _accompanyingExit = accompanyingExit ?? new Dictionary<int, StrategyOrderRequest>();
    }

    public string Name => "VerificationScripted";

    public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();

    public StrategyOrderRequest? Evaluate(StrategyContext context)
        => _primary.TryGetValue(context.BarIndex, out StrategyOrderRequest request) ? request : null;

    public StrategyOrderRequest? EvaluateExit(StrategyContext context)
        => _accompanyingExit.TryGetValue(context.BarIndex, out StrategyOrderRequest request) ? request : null;
}

/// <summary>
/// Random-but-reproducible strategy (all five order types, Long and Short, GTC and GTD) driven only by its own seeded
/// <see cref="Random"/> and the public <see cref="StrategyContext"/> - it never sees future bars. A NEW instance must be
/// created per run (the RNG is stateful).
/// </summary>
internal sealed class SeededRandomStrategy : IBacktestStrategy
{
    private const int EntryProbabilityPercent = 25;
    private const int ExitProbabilityPercent = 30;
    private const int PercentScale = 100;
    private const int OrderTypeCount = 5;
    private const int MaxOffsetCents = 200;
    private const decimal CentsPerUnit = 100m;
    private const int MaxGtdExtraBars = 3;
    private const decimal MinOrderPrice = 0.01m;

    private readonly Random _rng;

    private readonly bool _marketOrdersOnly;

    /// <param name="marketOrdersOnly">When true every request is a plain Market order (the RNG stream is consumed identically, so the entry/exit timing is unchanged).</param>
    public SeededRandomStrategy(int seed, bool marketOrdersOnly = false)
    {
        _rng = new Random(seed);
        _marketOrdersOnly = marketOrdersOnly;
    }

    public string Name => "VerificationSeededRandom";

    public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();

    public StrategyOrderRequest? Evaluate(StrategyContext context)
    {
        bool flat = context.PositionSide is null;
        int threshold = flat ? EntryProbabilityPercent : ExitProbabilityPercent;
        if (_rng.Next(PercentScale) >= threshold) return null;

        TradeSide side = flat
            ? (_rng.Next(2) == 0 ? TradeSide.Long : TradeSide.Short)
            : context.PositionSide!.Value;
        SignalType signal = (flat, side) switch
        {
            (true, TradeSide.Long) => SignalType.LongEntry,
            (true, TradeSide.Short) => SignalType.ShortEntry,
            (false, TradeSide.Long) => SignalType.LongExit,
            _ => SignalType.ShortExit,
        };
        // Entry Long / exit Short => Buy; entry Short / exit Long => Sell.
        bool buy = flat ? side == TradeSide.Long : side == TradeSide.Short;

        var drawnOrderType = (OrderType)_rng.Next(OrderTypeCount);
        OrderType orderType = _marketOrdersOnly ? OrderType.Market : drawnOrderType;
        decimal close = context.Bar.Close;
        decimal offset = _rng.Next(1, MaxOffsetCents + 1) / CentsPerUnit;
        decimal secondOffset = _rng.Next(1, MaxOffsetCents + 1) / CentsPerUnit;
        decimal? limit = null;
        decimal? stop = null;
        switch (orderType)
        {
            case OrderType.Limit:
                limit = Floor(buy ? close - offset : close + offset);
                break;
            case OrderType.Stop:
                stop = Floor(buy ? close + offset : close - offset);
                break;
            case OrderType.StopLimit:
                stop = Floor(buy ? close + offset : close - offset);
                limit = Floor(buy ? stop.Value + secondOffset : stop.Value - secondOffset);
                break;
        }

        TimeInForce tif = _rng.Next(2) == 0
            ? VerificationHarness.Gtc
            : new TimeInForce(false, context.BarIndex + 1 + _rng.Next(0, MaxGtdExtraBars + 1));

        return new StrategyOrderRequest(signal, orderType, limit, stop, tif, "seeded-random");
    }

    private static decimal Floor(decimal price) => Math.Max(MinOrderPrice, price);
}
