#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// T6 (A1 (Run fingerprint), owner decision G5): the additive, versioned canonical run fingerprint. Two runs share a fingerprint only
/// when every input, configuration and strategy setting, the full ordered output, the status and the diagnostic are identical. The legacy ReproducibilityHash is untouched.
/// Distinctness is checked on the exact preimage bytes as well as on the digest.
/// </summary>
public class BacktestRunFingerprintTests
{
    // Closes chosen so "Close > SMA(3)" opens a Long at bar 3 (fills bar 4), "Close < SMA(3)" closes it at bar 5 (fills bar 6) and the next entry is left open.
    private static readonly decimal[] Closes = { 100m, 100m, 100m, 110m, 112m, 90m, 88m, 100m, 110m, 95m };

    private static ImmutableArray<CandleData> Bars()
    {
        var bars = new List<CandleData>();
        for (int i = 0; i < Closes.Length; i++)
        {
            decimal open = i == 0 ? Closes[0] : Closes[i - 1];
            bars.Add(SyntheticBars.Bar(i, open, Math.Max(open, Closes[i]) + 1m, Math.Min(open, Closes[i]) - 1m, Closes[i]));
        }
        return bars.ToImmutableArray();
    }

    /// <summary>A built-in strategy, so its fingerprint is available (a custom strategy has none - see the dedicated test).</summary>
    private static ConditionBasedBacktestStrategy Strategy() => VerificationHarness.SmaTrendStrategy(3);

    private static BacktestConfiguration Config() => VerificationHarness.MakeConfig(initialCapital: 1000m, sizingParameter: 3m);

    private sealed record Run(BacktestRunFingerprint Fingerprint, byte[] Preimage, BacktestResult Result);

    /// <summary>Freeze, run (the real engine), seal: the intended sequence. Only conditions/entries the built-in manifest supports.</summary>
    private static Run Execute(BacktestInput input, BacktestConfiguration config, IBacktestStrategy strategy)
    {
        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(input, config, strategy);
        BacktestResult result = VerificationHarness.CreateEngine().Run(input, config, strategy);
        return new Run(frozen.Seal(result), frozen.EncodePreimage(result, null) ?? Array.Empty<byte>(), result);
    }

    private static Run Execute(ImmutableArray<CandleData> bars, BacktestConfiguration? config = null, IBacktestStrategy? strategy = null)
        => Execute(VerificationHarness.MakeInput(bars), config ?? Config(), strategy ?? Strategy());

    private static void AssertDiffers(Run baseline, Run variant, string what)
    {
        Assert.False(baseline.Preimage.AsSpan().SequenceEqual(variant.Preimage), $"preimage unchanged: {what}");
        Assert.NotEqual(baseline.Fingerprint, variant.Fingerprint);
    }

    [Fact]
    public void EqualExactInputs_GiveTheSameFingerprintAndPreimage()
    {
        Run a = Execute(Bars());
        Run b = Execute(Bars());

        Assert.True(a.Fingerprint.IsAvailable);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Equal(a.Fingerprint.ToHexString(), b.Fingerprint.ToHexString());
        Assert.True(a.Preimage.AsSpan().SequenceEqual(b.Preimage));
        Assert.Equal(1, a.Fingerprint.SchemaVersion);
        Assert.Equal(BacktestExecutionSemantics.Version, a.Fingerprint.ExecutionSemanticsVersion);
    }

    [Fact]
    public void PreimageStartsWithTheDomainTagSchemaVersionAndExecutionSemanticsVersion()
    {
        byte[] preimage = Execute(Bars()).Preimage;

        using var stream = new System.IO.MemoryStream(preimage);
        using var reader = new System.IO.BinaryReader(stream);
        Assert.True(reader.ReadBoolean());
        int tagLength = reader.ReadInt32();
        Assert.Equal("StockAnalyzer.Core.Backtest.RunFingerprint", System.Text.Encoding.UTF8.GetString(reader.ReadBytes(tagLength)));
        Assert.Equal(BacktestRunFingerprintBuilder.SchemaVersion, reader.ReadInt32());
        Assert.Equal(BacktestExecutionSemantics.Version, reader.ReadInt32());
    }

    [Fact]
    public void EveryBarField_IsPartOfTheIdentity()
    {
        Run baseline = Execute(Bars());

        foreach ((string what, Func<CandleData, CandleData> change) in new (string, Func<CandleData, CandleData>)[]
                 {
                     ("open", b => b with { Open = b.Open + 0.01m }),
                     ("high", b => b with { High = b.High + 0.01m }),
                     ("low", b => b with { Low = b.Low - 0.01m }),
                     ("close", b => b with { Close = b.Close - 0.01m }),
                     ("volume", b => b with { Volume = b.Volume + 1 }),
                     ("timestamp", b => b with { Timestamp = b.Timestamp.AddHours(1) }),
                 })
        {
            // The last bar only: no order interacts with it, so the change is confined to the frozen input (plus, for the timestamp, its equity point).
            ImmutableArray<CandleData> bars = Bars();
            bars = bars.SetItem(bars.Length - 1, change(bars[^1]));
            AssertDiffers(baseline, Execute(bars), what);
        }
    }

    [Fact]
    public void EveryInputAndConfigurationField_IsPartOfTheIdentity()
    {
        Run baseline = Execute(Bars());
        ImmutableArray<CandleData> bars = Bars();
        BacktestInput Input(string? symbol = null, TimeFrame? frame = null, DateTime? start = null, DateTime? end = null, int history = 0, int trading = 0)
            => new(bars, symbol ?? "TEST", frame ?? TimeFrame.D1, BacktestInput.CurrentDataVersion,
                start ?? SyntheticBars.Start, end ?? SyntheticBars.Start.AddDays(bars.Length + 1), history, trading);

        AssertDiffers(baseline, Execute(Input(symbol: "OTHER"), Config(), Strategy()), "symbol");
        AssertDiffers(baseline, Execute(Input(frame: TimeFrame.W1), Config(), Strategy()), "frame");
        AssertDiffers(baseline, Execute(Input(start: SyntheticBars.Start.AddDays(1)), Config(), Strategy()), "evaluation start");
        AssertDiffers(baseline, Execute(Input(end: SyntheticBars.Start.AddDays(99)), Config(), Strategy()), "evaluation end");
        AssertDiffers(baseline, Execute(Input(history: 1, trading: 1), Config(), Strategy()), "history/trading start");

        foreach ((string what, BacktestConfiguration config) in new (string, BacktestConfiguration)[]
                 {
                     ("initial capital", new() { InitialCapital = 2000m, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 3m, InitialMarginRatio = 1m, MaintenanceMarginRatio = 0.5m }),
                     ("commission flat", Cfg(c => c with { CommissionFlat = 1m })),
                     ("commission per unit", Cfg(c => c with { CommissionPerUnit = 0.1m })),
                     ("slippage", Cfg(c => c with { SlippageRatio = 0.001m })),
                     ("trading days", Cfg(c => c with { TradingDaysPerYear = 365 })),
                     ("sizing model", new() { InitialCapital = 1000m, SizingModel = PositionSizingModel.PercentOfEquity, SizingParameter = 0.1m, InitialMarginRatio = 1m, MaintenanceMarginRatio = 0.5m }),
                     ("sizing parameter", Cfg(c => c with { SizingParameter = 4m })),
                     ("initial margin", Cfg(c => c with { InitialMarginRatio = 0.9m })),
                     ("maintenance margin", Cfg(c => c with { MaintenanceMarginRatio = 0.4m })),
                     ("liquidation penalty", Cfg(c => c with { LiquidationPenaltyRatio = 0.01m })),
                 })
        {
            AssertDiffers(baseline, Execute(Input(), config, Strategy()), what);
        }
    }

    /// <summary>The configuration type is immutable (init-only), so a variant is a re-creation with one field changed.</summary>
    private static BacktestConfiguration Cfg(Func<ConfigFields, ConfigFields> change)
    {
        ConfigFields fields = change(ConfigFields.From(Config()));
        return new BacktestConfiguration
        {
            InitialCapital = fields.InitialCapital,
            CommissionFlat = fields.CommissionFlat,
            CommissionPerUnit = fields.CommissionPerUnit,
            SlippageRatio = fields.SlippageRatio,
            TradingDaysPerYear = fields.TradingDaysPerYear,
            SizingModel = fields.SizingModel,
            SizingParameter = fields.SizingParameter,
            InitialMarginRatio = fields.InitialMarginRatio,
            MaintenanceMarginRatio = fields.MaintenanceMarginRatio,
            LiquidationPenaltyRatio = fields.LiquidationPenaltyRatio,
        };
    }

    private sealed record ConfigFields(decimal InitialCapital, decimal CommissionFlat, decimal CommissionPerUnit, decimal SlippageRatio, int TradingDaysPerYear,
        PositionSizingModel SizingModel, decimal SizingParameter, decimal InitialMarginRatio, decimal MaintenanceMarginRatio, decimal LiquidationPenaltyRatio)
    {
        public static ConfigFields From(BacktestConfiguration c) => new(c.InitialCapital, c.CommissionFlat, c.CommissionPerUnit, c.SlippageRatio, c.TradingDaysPerYear,
            c.SizingModel, c.SizingParameter, c.InitialMarginRatio, c.MaintenanceMarginRatio, c.LiquidationPenaltyRatio);
    }

    [Fact]
    public void DecimalScale_IsPartOfTheEncoding_NoSemanticNormalizationIsClaimed()
    {
        Run scaleOne = Execute(Bars(), Cfg(c => c with { InitialCapital = 1000.0m }));
        Run scaleTwo = Execute(Bars(), Cfg(c => c with { InitialCapital = 1000.00m }));

        AssertDiffers(scaleOne, scaleTwo, "decimal scale");
    }

    [Fact]
    public void EveryOutputField_IsPartOfTheIdentity()
    {
        // A richer run: a rejected order (Short entry while a Long is held is a reversal, so use a StopLimit + GTD that expires) to populate most fields.
        Run baseline = Execute(Bars());
        BacktestResult result = baseline.Result;
        Assert.NotEmpty(result.Orders);
        Assert.NotEmpty(result.Fills);
        Assert.NotEmpty(result.Trades);
        Assert.NotEmpty(result.Signals);

        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(VerificationHarness.MakeInput(Bars()), Config(), Strategy());
        byte[] Preimage(BacktestResult r, BacktestRunDiagnostic? d = null) => frozen.EncodePreimage(r, d)!;
        byte[] basePreimage = Preimage(result);

        BacktestResult With(
            Func<BacktestOrder, BacktestOrder>? order = null, Func<BacktestFill, BacktestFill>? fill = null, Func<BacktestTrade, BacktestTrade>? trade = null,
            Func<EquityPoint, EquityPoint>? point = null, Func<BacktestSignal, BacktestSignal>? signal = null, RunStatus? status = null, bool? insufficient = null)
            => new(
                order is null ? result.Orders : result.Orders.SetItem(0, order(result.Orders[0])),
                fill is null ? result.Fills : result.Fills.SetItem(0, fill(result.Fills[0])),
                trade is null ? result.Trades : result.Trades.SetItem(0, trade(result.Trades[0])),
                point is null ? result.EquityPoints : result.EquityPoints.SetItem(0, point(result.EquityPoints[0])),
                signal is null ? result.Signals : result.Signals.SetItem(0, signal(result.Signals[0])),
                result.Configuration, status ?? result.Status, result.StrategyName, result.ReproducibilityHash, insufficient ?? result.IsInsufficientData);

        var variants = new (string What, BacktestResult Result)[]
        {
            ("order id", With(order: o => o with { OrderId = o.OrderId + 100 })),
            ("order side", With(order: o => o with { Side = OrderSide.Sell })),
            ("order type", With(order: o => o with { Type = OrderType.Limit })),
            ("order quantity", With(order: o => o with { Quantity = o.Quantity + 1 })),
            ("order limit", With(order: o => o with { LimitPrice = 1m })),
            ("order stop", With(order: o => o with { StopPrice = 1m })),
            ("order status", With(order: o => o with { Status = OrderStatus.Cancelled })),
            ("order TIF kind", With(order: o => o with { TimeInForce = new TimeInForce(false, 3) })),
            ("order TIF expiry", With(order: o => o with { TimeInForce = new TimeInForce(true, 7) })),
            ("order submitted bar", With(order: o => o with { SubmittedBar = o.SubmittedBar + 1 })),
            ("order earliest fill bar", With(order: o => o with { EarliestFillBar = o.EarliestFillBar + 1 })),
            ("order stop activated", With(order: o => o with { StopActivated = !o.StopActivated })),
            ("order expired reason", With(order: o => o with { ExpiredReason = ExpiredReason.Insolvency })),
            ("order rejected reason", With(order: o => o with { RejectedReason = RejectedReason.InvalidQuantity })),
            ("fill id", With(fill: f => f with { FillId = f.FillId + 100 })),
            ("fill order id", With(fill: f => f with { OrderId = f.OrderId + 100 })),
            ("fill bar", With(fill: f => f with { BarIndex = f.BarIndex + 1 })),
            ("fill time", With(fill: f => f with { FillTime = f.FillTime.AddHours(1) })),
            ("fill side", With(fill: f => f with { Side = OrderSide.Sell })),
            ("fill price", With(fill: f => f with { Price = f.Price + 0.01m })),
            ("fill quantity", With(fill: f => f with { Quantity = f.Quantity + 1 })),
            ("fill commission", With(fill: f => f with { Commission = f.Commission + 0.01m })),
            ("fill slippage amount", With(fill: f => f with { SlippageAmount = f.SlippageAmount + 0.01m })),
            ("trade id", With(trade: t => t with { TradeId = t.TradeId + 100 })),
            ("trade side", With(trade: t => t with { Side = TradeSide.Short })),
            ("trade entry bar", With(trade: t => t with { EntryBar = t.EntryBar + 1 })),
            ("trade entry time", With(trade: t => t with { EntryTime = t.EntryTime.AddHours(1) })),
            ("trade entry price", With(trade: t => t with { EntryPrice = t.EntryPrice + 0.01m })),
            ("trade exit bar", With(trade: t => t with { ExitBar = t.ExitBar + 1 })),
            ("trade exit time", With(trade: t => t with { ExitTime = t.ExitTime.AddHours(1) })),
            ("trade exit price", With(trade: t => t with { ExitPrice = t.ExitPrice + 0.01m })),
            ("trade quantity", With(trade: t => t with { Quantity = t.Quantity + 1 })),
            ("trade gross", With(trade: t => t with { ClosedGross = t.ClosedGross + 0.01m })),
            ("trade net", With(trade: t => t with { ClosedNet = t.ClosedNet + 0.01m })),
            ("trade entry fee", With(trade: t => t with { EntryFee = t.EntryFee + 0.01m })),
            ("trade exit fee", With(trade: t => t with { ExitFee = t.ExitFee + 0.01m })),
            ("trade holding bars", With(trade: t => t with { HoldingBars = t.HoldingBars + 1 })),
            ("trade forced liquidation", With(trade: t => t with { IsForcedLiquidation = !t.IsForcedLiquidation })),
            ("equity bar", With(point: p => p with { BarIndex = p.BarIndex + 1 })),
            ("equity timestamp", With(point: p => p with { Timestamp = p.Timestamp.AddHours(1) })),
            ("equity value", With(point: p => p with { Equity = p.Equity + 0.01m })),
            ("equity cash", With(point: p => p with { Cash = p.Cash + 0.01m })),
            ("equity market value", With(point: p => p with { MarketValue = p.MarketValue + 0.01m })),
            ("equity held margin", With(point: p => p with { HeldMargin = p.HeldMargin + 0.01m })),
            ("signal type", With(signal: s => s with { Type = SignalType.ShortEntry })),
            ("signal bar", With(signal: s => s with { BarIndex = s.BarIndex + 1 })),
            ("signal time", With(signal: s => s with { SignalTime = s.SignalTime.AddHours(1) })),
            ("signal reason", With(signal: s => s with { Reason = s.Reason + "x" })),
            ("status", With(status: RunStatus.Insolvent)),
            ("insufficient data flag", With(insufficient: true)),
            // The strategy name is deliberately not varied here: a result of another strategy is rejected before encoding (see SealingAResultOfADifferentStrategy_IsRejected).
        };

        foreach ((string what, BacktestResult variant) in variants)
        {
            Assert.False(basePreimage.AsSpan().SequenceEqual(Preimage(variant)), $"preimage unchanged: {what}");
        }

        // Adding or removing an element changes the counts and therefore the encoding.
        BacktestResult fewerFills = new(result.Orders, result.Fills.RemoveAt(0), result.Trades, result.EquityPoints, result.Signals, result.Configuration, result.Status, result.StrategyName, result.ReproducibilityHash, result.IsInsufficientData);
        Assert.False(basePreimage.AsSpan().SequenceEqual(Preimage(fewerFills)));
    }

    [Fact]
    public void Diagnostic_IsPartOfTheIdentity_ButItsDisplayMessageIsNot()
    {
        Run baseline = Execute(Bars());
        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(VerificationHarness.MakeInput(Bars()), Config(), Strategy());

        var diagnostic = new BacktestRunDiagnostic(BacktestDiagnosticCode.ArithmeticOverflow, BacktestRunPhase.Bar, 3, BacktestArithmeticOperation.MarkToMarket, "message one");
        byte[] none = frozen.EncodePreimage(baseline.Result, null)!;
        byte[] withDiagnostic = frozen.EncodePreimage(baseline.Result, diagnostic)!;
        byte[] otherMessage = frozen.EncodePreimage(baseline.Result, diagnostic with { Message = "message two" })!;

        Assert.False(none.AsSpan().SequenceEqual(withDiagnostic));
        Assert.True(withDiagnostic.AsSpan().SequenceEqual(otherMessage));
        foreach (BacktestRunDiagnostic changed in new[]
                 {
                     diagnostic with { Phase = BacktestRunPhase.Preparation, BarIndex = null },
                     diagnostic with { BarIndex = 4 },
                     diagnostic with { Operation = BacktestArithmeticOperation.ExitAccounting },
                 })
        {
            Assert.False(withDiagnostic.AsSpan().SequenceEqual(frozen.EncodePreimage(baseline.Result, changed)!));
        }
    }

    [Fact]
    public void StrategySettings_ArePartOfTheIdentity_IncludingConditionOrderAndRiskManagement()
    {
        ImmutableArray<CandleData> bars = Enumerable.Range(0, 30)
            .Select(i => SyntheticBars.Bar(i, 100m + i % 5, 103m + i % 5, 98m, 100m + (i * 7 % 5)))
            .ToImmutableArray();

        Run period3 = Execute(bars, strategy: VerificationHarness.SmaTrendStrategy(3));
        Run period3Again = Execute(bars, strategy: VerificationHarness.SmaTrendStrategy(3));
        Run period5 = Execute(bars, strategy: VerificationHarness.SmaTrendStrategy(5));
        Run withRisk = Execute(bars, strategy: new ConditionBasedBacktestStrategy(Entries(3), new BacktestRiskManagementSettings { StopLossPercent = 0.05m }));
        Run withOtherRisk = Execute(bars, strategy: new ConditionBasedBacktestStrategy(Entries(3), new BacktestRiskManagementSettings { StopLossPercent = 0.06m }));
        Run reordered = Execute(bars, strategy: new ConditionBasedBacktestStrategy(Entries(3).Reverse().ToArray()));

        Assert.True(period3.Fingerprint.IsAvailable);
        Assert.Equal(period3.Fingerprint, period3Again.Fingerprint);
        AssertDiffers(period3, period5, "indicator parameter");
        AssertDiffers(period3, withRisk, "risk management");
        AssertDiffers(withRisk, withOtherRisk, "risk management value");
        AssertDiffers(period3, reordered, "condition order");
    }

    private static BacktestConditionEntry[] Entries(int period)
    {
        BacktestConditionSide Close() => new() { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close };
        BacktestConditionSide Sma() => new() { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = period } };
        return new[]
        {
            new BacktestConditionEntry { Left = Close(), Operator = ComparisonOperator.GreaterThan, TargetMode = RightHandTargetMode.Indicator, Right = Sma(), Role = BacktestConditionRole.EntryOnly, Position = TradeSide.Long },
            new BacktestConditionEntry { Left = Close(), Operator = ComparisonOperator.LessThan, TargetMode = RightHandTargetMode.Indicator, Right = Sma(), Role = BacktestConditionRole.ExitOnly },
        };
    }

    [Fact]
    public void ACustomStrategy_HasNoCompleteFingerprint_AndItsNameIsNeverSubstituted()
    {
        BacktestInput input = VerificationHarness.MakeInput(Bars());
        ScriptedStrategy custom = new(new Dictionary<int, StrategyOrderRequest> { [0] = VerificationHarness.Req(SignalType.LongEntry) });

        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(input, Config(), custom);
        BacktestResult result = VerificationHarness.CreateEngine().Run(input, Config(), custom);
        BacktestRunFingerprint fingerprint = frozen.Seal(result);

        Assert.False(fingerprint.IsAvailable);
        Assert.Contains(typeof(ScriptedStrategy).FullName!, fingerprint.UnavailableReason);
        Assert.Empty(fingerprint.GetHash());
        Assert.Null(frozen.EncodePreimage(result, null));
    }

    [Fact]
    public void ANoOpStrategy_IsIdentifiedByItsRequestedIndicators()
    {
        StrategyIndicatorRequest Request(int period) => new("k", IndicatorType.SMA, new CoreSmaParameter { Period = period });
        ImmutableArray<CandleData> bars = Bars();

        Run a = Execute(bars, strategy: new NoOpBacktestStrategy(new[] { Request(3) }));
        Run same = Execute(bars, strategy: new NoOpBacktestStrategy(new[] { Request(3) }));
        Run other = Execute(bars, strategy: new NoOpBacktestStrategy(new[] { Request(4) }));

        Assert.True(a.Fingerprint.IsAvailable);
        Assert.Equal(a.Fingerprint, same.Fingerprint);
        AssertDiffers(a, other, "requested indicator parameter");
    }

    [Fact]
    public void ANoOpStrategy_ExposesItsRequestsAsData_AndThatIsWhatTheManifestEncodes()
    {
        StrategyIndicatorRequest[] requests = { new("k", IndicatorType.SMA, new CoreSmaParameter { Period = 3 }) };
        var noOp = new NoOpBacktestStrategy(requests);

        Assert.Same(requests, noOp.RequiredIndicators);
        Assert.Same(noOp.RequiredIndicators, noOp.GetRequiredIndicators());
    }

    [Fact]
    public void ForeignFrameMap_IsSortedByFrame_SoInsertionOrderDoesNotMatter()
    {
        ImmutableArray<CandleData> bars = Bars();
        ImmutableArray<CandleData> weekly = ImmutableArray.Create(SyntheticBars.Bar(0, 100m, 101m, 99m, 100m));
        ImmutableArray<CandleData> hourly = ImmutableArray.Create(SyntheticBars.Bar(0, 50m, 51m, 49m, 50m));

        BacktestInput With(params KeyValuePair<TimeFrame, ImmutableArray<CandleData>>[] frames)
            => new(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, SyntheticBars.Start, SyntheticBars.Start.AddDays(9), 0, 0, new Dictionary<TimeFrame, ImmutableArray<CandleData>>(frames));

        var w = new KeyValuePair<TimeFrame, ImmutableArray<CandleData>>(TimeFrame.W1, weekly);
        var h = new KeyValuePair<TimeFrame, ImmutableArray<CandleData>>(TimeFrame.H1, hourly);

        Run forward = Execute(With(w, h), Config(), Strategy());
        Run backward = Execute(With(h, w), Config(), Strategy());
        Run without = Execute(new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, SyntheticBars.Start, SyntheticBars.Start.AddDays(9), 0, 0), Config(), Strategy());

        Assert.Equal(forward.Fingerprint, backward.Fingerprint);
        AssertDiffers(forward, without, "foreign frames present");
    }

    [Fact]
    public void MutatingTheSourceMapAfterFreeze_CannotChangeTheFrozenIdentity()
    {
        ImmutableArray<CandleData> bars = Bars();
        ImmutableArray<CandleData> weekly = ImmutableArray.Create(SyntheticBars.Bar(0, 100m, 101m, 99m, 100m));
        var map = new Dictionary<TimeFrame, ImmutableArray<CandleData>> { [TimeFrame.W1] = weekly };
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, SyntheticBars.Start, SyntheticBars.Start.AddDays(9), 0, 0, map);

        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(input, Config(), Strategy());
        BacktestResult result = VerificationHarness.CreateEngine().Run(input, Config(), Strategy());
        BacktestRunFingerprint before = frozen.Seal(result);

        map[TimeFrame.H1] = ImmutableArray.Create(SyntheticBars.Bar(0, 1m, 2m, 1m, 1m)); // the caller mutates its own dictionary afterwards

        Assert.Equal(before, frozen.Seal(result));
    }

    [Fact]
    public void EmptyRuns_DifferWhenTheirInputsDiffer_WhileTheLegacyHashCannotTellThemApart()
    {
        BacktestInput Empty(string symbol) => new(ImmutableArray<CandleData>.Empty, symbol, TimeFrame.D1, BacktestInput.CurrentDataVersion, SyntheticBars.Start, SyntheticBars.Start.AddDays(1), 0, 0);

        Run a = Execute(Empty("AAA"), Config(), Strategy());
        Run b = Execute(Empty("BBB"), Config(), Strategy());

        Assert.True(a.Result.IsInsufficientData);
        Assert.Equal(a.Result.ReproducibilityHash, b.Result.ReproducibilityHash); // legacy CreateEmpty is a config-only placeholder: unchanged
        AssertDiffers(a, b, "symbol of an empty run");
    }

    [Fact]
    public void LegacyReproducibilityHash_IsUntouchedBySealing()
    {
        BacktestInput input = VerificationHarness.MakeInput(Bars());
        BacktestResult result = VerificationHarness.CreateEngine().Run(input, Config(), Strategy());
        byte[] legacyBefore = (byte[])result.ReproducibilityHash.Clone();

        _ = BacktestRunFingerprintBuilder.Freeze(input, Config(), Strategy()).Seal(result);

        Assert.Equal(legacyBefore, result.ReproducibilityHash);
        Assert.Equal(32, result.ReproducibilityHash.Length);
    }

    [Fact]
    public void ExposedBytes_AreDefensiveCopies()
    {
        BacktestRunFingerprint fingerprint = Execute(Bars()).Fingerprint;
        byte[] first = fingerprint.GetHash();
        string hexBefore = fingerprint.ToHexString();

        first[0] ^= 0xFF;

        Assert.Equal(hexBefore, fingerprint.ToHexString());
        Assert.NotEqual(first, fingerprint.GetHash());
    }

    [Fact]
    public void ADiagnosedFailedRun_HasItsOwnFingerprint()
    {
        // The entry signalled at bar 3 fills at bar 4's Open 5e28 and 3 * 5e28 overflows the entry notional.
        decimal[] closes = { 100m, 100m, 100m, 110m, 5e28m };
        BacktestInput input = VerificationHarness.MakeInput(closes.Select((close, i) => SyntheticBars.Bar(i, close, close, close, close)).ToImmutableArray());
        ConditionBasedBacktestStrategy strategy = Strategy();

        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(input, Config(), strategy);
        BacktestDiagnosedRun failed = VerificationHarness.CreateEngine().RunWithDiagnostics(input, Config(), strategy);

        Assert.Equal(RunStatus.Failed, failed.Result.Status);
        Assert.NotNull(failed.Diagnostic);
        Assert.False(frozen.EncodePreimage(failed.Result, failed.Diagnostic)!.AsSpan().SequenceEqual(frozen.EncodePreimage(failed.Result, null)!));
        Assert.True(frozen.Seal(failed.Result, failed.Diagnostic).IsAvailable);
    }

    [Fact]
    public void SealingAResultOfADifferentConfiguration_IsRejected_ForTheSealAndThePreimage()
    {
        BacktestInput input = VerificationHarness.MakeInput(Bars());
        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(input, Config(), Strategy());
        BacktestConfiguration other = VerificationHarness.MakeConfig(initialCapital: 2000m, sizingParameter: 3m);
        BacktestResult foreign = VerificationHarness.CreateEngine().Run(input, other, Strategy());

        Assert.Equal("result", Assert.Throws<ArgumentException>(() => frozen.Seal(foreign)).ParamName);
        Assert.Throws<ArgumentException>(() => frozen.EncodePreimage(foreign, null));
    }

    [Fact]
    public void SealingAResultOfADifferentStrategy_IsRejected()
    {
        BacktestInput input = VerificationHarness.MakeInput(Bars());
        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(input, Config(), Strategy());
        BacktestResult foreign = VerificationHarness.CreateEngine().Run(input, Config(), new NoOpBacktestStrategy(Array.Empty<StrategyIndicatorRequest>()));

        Assert.Throws<ArgumentException>(() => frozen.Seal(foreign));
    }

    [Fact]
    public void CompatibilitySeal_CannotRejectSameConfigurationAndStrategyNameFromDifferentBars()
    {
        ImmutableArray<CandleData> frozenBars = Bars();
        ImmutableArray<CandleData> otherBars = frozenBars.SetItem(
            frozenBars.Length - 1,
            frozenBars[^1] with { Close = frozenBars[^1].Close + 1m });
        BacktestConfiguration configuration = Config();
        ConditionBasedBacktestStrategy strategy = Strategy();
        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(
            VerificationHarness.MakeInput(frozenBars),
            configuration,
            strategy);
        BacktestResult foreign = VerificationHarness.CreateEngine().Run(
            VerificationHarness.MakeInput(otherBars),
            configuration,
            strategy);

        Assert.True(frozen.Seal(foreign).IsAvailable);
    }

    [Fact]
    public void AnUnavailableFingerprintBuilder_StillRejectsAResultOfAnotherRun()
    {
        BacktestInput input = VerificationHarness.MakeInput(Bars());
        ScriptedStrategy custom = new(new Dictionary<int, StrategyOrderRequest> { [0] = VerificationHarness.Req(SignalType.LongEntry) });
        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(input, Config(), custom);
        BacktestResult foreign = VerificationHarness.CreateEngine().Run(input, Config(), Strategy());

        Assert.Throws<ArgumentException>(() => frozen.Seal(foreign));
    }

    [Fact]
    public void AZeroBarInputResult_CarriesNoStrategyName_YetBelongsToItsFrozenRun_ButNotToAnotherConfiguration()
    {
        BacktestInput empty = new(ImmutableArray<CandleData>.Empty, "AAA", TimeFrame.D1, BacktestInput.CurrentDataVersion, SyntheticBars.Start, SyntheticBars.Start.AddDays(1), 0, 0);
        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(empty, Config(), Strategy());
        BacktestResult own = VerificationHarness.CreateEngine().Run(empty, Config(), Strategy());
        BacktestResult foreign = VerificationHarness.CreateEngine().Run(empty, VerificationHarness.MakeConfig(initialCapital: 2000m, sizingParameter: 3m), Strategy());

        Assert.Equal(string.Empty, own.StrategyName);
        Assert.True(frozen.Seal(own).IsAvailable);
        Assert.Throws<ArgumentException>(() => frozen.Seal(foreign));
    }
}
