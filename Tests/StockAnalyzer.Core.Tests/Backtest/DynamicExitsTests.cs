#nullable enable
using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class DynamicExitsTests
{
    private static BacktestConditionEntry CloseAbove(decimal threshold) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = threshold,
        Role = BacktestConditionRole.EntryOnly,
        Position = TradeSide.Long,
    };

    private static BacktestConditionEntry CloseBelow(decimal threshold) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
        Operator = ComparisonOperator.LessThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = threshold,
        Role = BacktestConditionRole.EntryOnly,
        Position = TradeSide.Short,
    };

    private static BacktestConfiguration DefaultConfig() => VerificationHarness.MakeConfig();

    [Fact]
    public void DynamicExits_WhenNull_PreservesGoldenFingerprintAndResults()
    {
        // Proof obligation (a): dynamicExits == null reproduces existing run and fingerprint byte-for-byte.
        var bars = SyntheticBars.FromTable(
            (100m, 101m, 99m, 100m),
            (100m, 106m, 99m, 105m),
            (100m, 105m, 98m, 102m),
            (100m, 103m, 97m, 99m),
            (99m, 101m, 95m, 96m),
            (96m, 98m, 94m, 95m));

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategyWithoutDynamic = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(102m) },
            new BacktestRiskManagementSettings { StopLossPercent = 0.05m, TakeProfitPercent = 0.10m });

        var strategyWithNullDynamic = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(102m) },
            new BacktestRiskManagementSettings
            {
                StopLossPercent = 0.05m,
                TakeProfitPercent = 0.10m,
                DynamicExits = null,
            });

        var engine = VerificationHarness.CreateEngine();
        var result1 = engine.Run(input, config, strategyWithoutDynamic);
        var result2 = engine.Run(input, config, strategyWithNullDynamic);

        Assert.Equal(result1.Trades.Length, result2.Trades.Length);
        Assert.Equal(result1.ReproducibilityHash, result2.ReproducibilityHash);

        var fp1 = BacktestRunFingerprintBuilder.Freeze(input, config, strategyWithoutDynamic).Seal(result1);
        var fp2 = BacktestRunFingerprintBuilder.Freeze(input, config, strategyWithNullDynamic).Seal(result2);

        Assert.Equal(fp1.ToHexString(), fp2.ToHexString());
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void TimeStop_FillsAtOpen_AfterMaxHoldingBars()
    {
        // Proof obligation (b): Hand-calculated Time Stop test with small synthetic candles.
        // Bar 0: C=100.
        // Bar 1: C=105 -> Entry signal emitted at Bar 1 Close.
        // Bar 2: O=100 -> Entry fills at Bar 2 Open (e = 2, EntryPrice = 100, heldBars = 0).
        // Bar 3: heldBars = 3 - 2 = 1.
        // Bar 4: heldBars = 4 - 2 = 2. When maxHoldingBars = 2, at Bar 4 Close Time Stop triggers!
        // Bar 5: Fills at Bar 5 Open (98m) as Market exit.
        var bars = SyntheticBars.FromTable(
            (100m, 101m, 99m, 100m),  // 0
            (100m, 106m, 99m, 105m),  // 1: Signal
            (100m, 102m, 99m, 101m),  // 2: Entry fill at 100
            (101m, 102m, 99m, 100m),  // 3: held = 1
            (100m, 102m, 99m, 101m),  // 4: held = 2 -> Time Stop signal at Close
            (98m, 100m, 97m, 99m),    // 5: Exit fill at Open 98
            (99m, 100m, 98m, 99m));   // 6

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(102m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    MaxHoldingBars = 2,
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);
        var trade = Assert.Single(result.Trades);

        Assert.Equal(2, trade.EntryBar);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(5, trade.ExitBar);
        Assert.Equal(98m, trade.ExitPrice);
        Assert.Equal("Time Stop", trade.ExitReason);
        Assert.Null(trade.StopLevelAtExit);
    }

    [Fact]
    public void AtrStop_AnchorsToSignalBar_AndTriggersAtExactPrice()
    {
        // Proof obligation (b): Hand-calculated ATR Stop.
        // ATR(2) requires 2 warmup bars of TR:
        // Bar 0: O=100, H=100, L=100, C=100
        // Bar 1: O=100, H=105, L=95, C=100 (TR(1) = 10)
        // Bar 2: O=100, H=105, L=95, C=102 (TR(2) = 10) -> ATR(2) = 10.0m. Close=102 > 101 -> Signal!
        // Bar 3: O=100, H=101, L=98, C=100 -> Entry fills at 100 (`e = 3`).
        // Anchored ATR at signal bar (e-1 = 2) is 10.0m.
        // Multiple = 1.0m => ATR Stop Price = 100 - 1.0 * 10.0 = 90.0m.
        // Bar 4: O=95, H=96, L=89, C=92 -> Low reaches 89 <= 90.0m -> Stop breach!
        // Bar 5: O=88, H=91, L=85, C=88 -> Fills at Open (88m).
        var bars = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 105m, 95m, 100m),   // 1
            (100m, 105m, 95m, 102m),   // 2: Signal! ATR(2) = 10.0m
            (100m, 101m, 98m, 100m),   // 3: Entry fill at 100. Stop = 90.0m.
            (95m, 96m, 89m, 92m),      // 4: Low reaches 89 <= 90 -> Stop breach!
            (88m, 91m, 85m, 88m));     // 5: Fill at 88

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(101m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrStop = new AtrExitParameters { Period = 2, Multiple = 1.0m },
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);
        var trade = Assert.Single(result.Trades);

        Assert.Equal(3, trade.EntryBar);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(5, trade.ExitBar);
        Assert.Equal("ATR Stop", trade.ExitReason);
        Assert.Equal(90.0m, trade.StopLevelAtExit);
    }

    [Fact]
    public void AtrTarget_AnchorsToSignalBar_AndTriggersAtExactPrice()
    {
        // Proof obligation (b): Hand-calculated ATR Target.
        // Bar 0: O=100, H=100, L=100, C=100
        // Bar 1: O=100, H=105, L=95, C=100 (TR(1) = 10)
        // Bar 2: O=100, H=105, L=95, C=102 (TR(2) = 10) -> ATR(2) = 10.0m. Signal!
        // Multiple = 1.0m => ATR Target Price = 100 + 1.0 * 10.0 = 110.0m.
        // Bar 3: O=100, H=101, L=98, C=100 -> Entry fills at 100 (`e = 3`).
        // Bar 4: O=105, H=112, L=104, C=108 -> High reaches 112 >= 110 -> Target breach!
        // Bar 5: O=110, H=114, L=109, C=112 -> Fills at Limit (110).
        var bars = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 105m, 95m, 100m),   // 1
            (100m, 105m, 95m, 102m),   // 2: Signal! ATR(2) = 10.0m
            (100m, 101m, 98m, 100m),   // 3: Entry at 100
            (105m, 112m, 104m, 108m),  // 4: High reaches 112 >= 110 -> Target breach!
            (110m, 114m, 109m, 112m)); // 5: Fill at 110

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(101m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrTarget = new AtrExitParameters { Period = 2, Multiple = 1.0m },
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);
        var trade = Assert.Single(result.Trades);

        Assert.Equal(3, trade.EntryBar);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(5, trade.ExitBar);
        Assert.Equal("ATR Target", trade.ExitReason);
    }

    [Fact]
    public void Coexistence_FixedStopAndAtrStop_SelectsTighterStop()
    {
        // Proof obligation (b): When both fixed SL and ATR Stop are present, the tighter stop price wins.
        // Entry at 100.
        // Fixed SL: 15% => Stop = 85.0m.
        // ATR(2) = 10.0m, Multiple = 1.0m => ATR Stop = 90.0m.
        // 90.0m > 85.0m => 90.0m is tighter and must win!
        var bars = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 105m, 95m, 100m),   // 1
            (100m, 105m, 95m, 102m),   // 2: Signal! ATR(2) = 10.0m
            (100m, 101m, 98m, 100m),   // 3: Entry at 100. ATR Stop=90, Fixed SL=85. Effective=90.
            (92m, 93m, 88m, 91m),      // 4: Low reaches 88 <= 90 (breaches ATR Stop, but NOT 85)
            (87m, 89m, 86m, 87m));     // 5: Fill at 87

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(101m) },
            new BacktestRiskManagementSettings
            {
                StopLossPercent = 0.15m,
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrStop = new AtrExitParameters { Period = 2, Multiple = 1.0m },
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);
        var trade = Assert.Single(result.Trades);

        Assert.Equal("ATR Stop", trade.ExitReason);
        Assert.Equal(90.0m, trade.StopLevelAtExit);
    }

    [Fact]
    public void MutationTest_PostSignalBarsDoNotAlterAtrSignalLevel()
    {
        // Proof obligation (c): Mutating bars after the signal bar does not alter ATR_signal.
        // Signal bar: Bar 2, ATR(2) = 10.0m. Stop = 100 - 1.0 * 10 = 90.0m.
        // Run A: Normal bars after signal.
        var barsA = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 105m, 95m, 100m),   // 1
            (100m, 105m, 95m, 102m),   // 2: Signal bar: ATR(2) = 10.0m
            (100m, 102m, 99m, 101m),   // 3: Entry at 100
            (92m, 93m, 89m, 90m),      // 4: Breaches 90
            (88m, 90m, 87m, 88m));     // 5: Exit

        // Run B: Bar 3 has massive volatility (H=150, L=98). If ATR were dynamically recomputed, stop would change.
        var barsB = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 105m, 95m, 100m),   // 1
            (100m, 105m, 95m, 102m),   // 2: Signal bar: ATR(2) = 10.0m
            (100m, 150m, 98m, 101m),   // 3: Massive range on Bar 3
            (92m, 93m, 89m, 90m),      // 4: Breaches 90
            (88m, 90m, 87m, 88m));     // 5: Exit

        var config = DefaultConfig();
        var strategyA = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(101m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrStop = new AtrExitParameters { Period = 2, Multiple = 1.0m },
                },
            });
        var strategyB = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(101m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrStop = new AtrExitParameters { Period = 2, Multiple = 1.0m },
                },
            });

        var resultA = VerificationHarness.Run(VerificationHarness.MakeInput(barsA), config, strategyA);
        var resultB = VerificationHarness.Run(VerificationHarness.MakeInput(barsB), config, strategyB);

        var tradeA = Assert.Single(resultA.Trades);
        var tradeB = Assert.Single(resultB.Trades);

        Assert.Equal(tradeA.StopLevelAtExit, tradeB.StopLevelAtExit);
        Assert.Equal(90.0m, tradeB.StopLevelAtExit);
    }

    [Fact]
    public void InsufficientWarmup_EmitsDynamicExitAtrUnavailableWarning_DoesNotBlockEntry()
    {
        // Proof obligation (d): Warning is emitted when ATR cannot be calculated, trade entry still executes.
        // ATR Period = 14, but only 4 bars exist -> ATR is null.
        var bars = SyntheticBars.FromTable(
            (100m, 101m, 99m, 100m),
            (100m, 106m, 99m, 105m),   // Signal
            (100m, 101m, 98m, 100m),   // Entry
            (100m, 101m, 98m, 100m));  // Holding

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(101m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrStop = new AtrExitParameters { Period = 14, Multiple = 2.0m },
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);

        // Entry should have occurred on bar 2:
        Assert.NotEmpty(result.Orders);
        Assert.Single(result.Warnings);
        Assert.Equal(WarningCode.DynamicExitAtrUnavailable, result.Warnings[0].Code);
    }

    [Fact]
    public void TrailingStop_AtrMultiple_RatchetsUpward_AndExitsAtStopLevel()
    {
        // Proof obligation (b): Hand-calculated Long Trailing Stop with AtrMultiple.
        // ATR(2) = 10.0m. Multiple = 1.0m. offset = 10.0m.
        // Bar 0: O=100, H=100, L=100, C=100
        // Bar 1: O=100, H=105, L=95, C=100 (TR(1) = 10)
        // Bar 2: O=100, H=105, L=95, C=102 (TR(2) = 10) -> ATR(2) = 10.0m. Signal at Close!
        // Bar 3: O=100, H=108, L=99, C=106 -> Entry fills at 100 ( = 3).
        //   H_3 = 108. L_3 = max(100 - 10, 108 - 10) = 98.0m. (Active on Bar 4).
        // Bar 4: O=106, H=115, L=104, C=114 -> Active stop is 98.0m. Low 104 > 98 (no breach).
        //   H_4 = 115. L_4 = max(98, 115 - 10) = 105.0m. (Active on Bar 5).
        // Bar 5: O=112, H=113, L=103, C=105 -> Active stop is 105.0m. Low 103 <= 105 (BREACH!).
        // Bar 6: O=102, H=104, L=100, C=101 -> Fills at Open 102.
        var bars = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 105m, 95m, 100m),   // 1
            (100m, 105m, 95m, 102m),   // 2: Signal!
            (100m, 108m, 99m, 106m),   // 3: Entry at 100
            (106m, 115m, 104m, 114m),  // 4: Ratchets to 105
            (112m, 113m, 103m, 105m),  // 5: Low reaches 103 <= 105 -> Breach!
            (102m, 104m, 100m, 101m)); // 6: Fill at 102

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(101m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    TrailingStop = new TrailingExitParameters
                    {
                        Mode = TrailingExitMode.AtrMultiple,
                        Period = 2,
                        Multiple = 1.0m,
                    },
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);
        var trade = Assert.Single(result.Trades);

        Assert.Equal(3, trade.EntryBar);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(6, trade.ExitBar);
        Assert.Equal(102m, trade.ExitPrice);
        Assert.Equal("Trailing Stop", trade.ExitReason);
        Assert.Equal(105.0m, trade.StopLevelAtExit);
    }

    [Fact]
    public void TrailingStop_Ratio_RatchetsUpward_AndExitsAtStopLevel()
    {
        // Proof obligation (b): Hand-calculated Long Trailing Stop with Ratio mode (ratio = 0.10).
        // Bar 0: C=100
        // Bar 1: C=105 -> Signal
        // Bar 2: O=100, H=110, L=99, C=108 -> Entry at 100 ( = 2).
        //   H_2 = 110. offset = 0.10 * 110 = 11.0m. L_2 = max(89, 110 - 11) = 99.0m. (Active on Bar 3).
        // Bar 3: O=108, H=120, L=105, C=118 -> Active stop is 99.0m. Low 105 > 99 (no breach).
        //   H_3 = 120. offset = 0.10 * 120 = 12.0m. L_3 = max(99, 120 - 12) = 108.0m. (Active on Bar 4).
        // Bar 4: O=115, H=116, L=106, C=107 -> Active stop is 108.0m. Low 106 <= 108 (BREACH!).
        // Bar 5: O=104, H=106, L=102, C=103 -> Fills at Open 104.
        var bars = SyntheticBars.FromTable(
            (100m, 101m, 99m, 100m),   // 0
            (100m, 106m, 99m, 105m),   // 1: Signal
            (100m, 110m, 99m, 108m),   // 2: Entry at 100
            (108m, 120m, 105m, 118m),  // 3: Ratchets to 108
            (115m, 116m, 106m, 107m),  // 4: Low reaches 106 <= 108 -> Breach!
            (104m, 106m, 102m, 103m)); // 5: Fill at 104

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(102m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    TrailingStop = new TrailingExitParameters
                    {
                        Mode = TrailingExitMode.Ratio,
                        Ratio = 0.10m,
                    },
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);
        var trade = Assert.Single(result.Trades);

        Assert.Equal(2, trade.EntryBar);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(5, trade.ExitBar);
        Assert.Equal(104m, trade.ExitPrice);
        Assert.Equal("Trailing Stop", trade.ExitReason);
        Assert.Equal(108.0m, trade.StopLevelAtExit);
    }

    [Fact]
    public void TrailingStop_ActiveFromBarTPlus1_DoesNotExitOnEntryBar()
    {
        // Proof obligation (b): Invariant that Trailing Stop is active from bar t+1, NOT on entry bar t.
        // Bar 2 is entry bar ( = 2). Entry fills at 100m.
        // Low reaches 85m. With ratio = 0.10, offset = 10, stop level would be 90m.
        // But because trailing stop is only active from bar t+1, Bar 2 Low does NOT trigger an exit.
        var bars = SyntheticBars.FromTable(
            (100m, 101m, 99m, 100m),   // 0
            (100m, 106m, 99m, 105m),   // 1: Signal
            (100m, 102m, 85m, 95m),    // 2: Entry at 100, Low drops to 85. No exit!
            (95m, 98m, 92m, 96m),      // 3: Active stop on Bar 3 is 90.0m. Low 92 > 90 -> no breach!
            (95m, 96m, 88m, 90m),      // 4: Low reaches 88 <= 90 -> Breach!
            (87m, 89m, 85m, 86m));     // 5: Exit fill

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(102m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    TrailingStop = new TrailingExitParameters
                    {
                        Mode = TrailingExitMode.Ratio,
                        Ratio = 0.10m,
                    },
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);
        var trade = Assert.Single(result.Trades);

        Assert.Equal(2, trade.EntryBar);
        Assert.Equal(5, trade.ExitBar);
        Assert.Equal("Trailing Stop", trade.ExitReason);
    }

    [Fact]
    public void Coexistence_TrailingStopTakesPrecedenceWhenTighter()
    {
        // Proof obligation (b): When Fixed SL, ATR Stop, and Trailing Stop coexist, the tightest wins.
        // Entry at 100.
        // Fixed SL: 15% => 85.0m.
        // ATR(2) = 10.0m, Multiple = 1.0m => ATR Stop = 90.0m.
        // Trailing Stop: Ratio = 0.05m. At Bar 3, High reaches 110m.
        // Trailing Stop Level = 110 * (1 - 0.05) = 104.5m > 90.0m > 85.0m!
        // When Bar 4 Low dips to 102m <= 104.5m:
        // Trailing Stop triggers with reason "Trailing Stop"!
        var bars = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 105m, 95m, 100m),   // 1
            (100m, 105m, 95m, 102m),   // 2: Signal! ATR(2) = 10.0m
            (100m, 110m, 98m, 108m),   // 3: Entry at 100. High=110. Trailing stop for Bar 4 = 104.5m.
            (107m, 108m, 102m, 105m),  // 4: Low reaches 102 <= 104.5 -> Trailing Stop breach!
            (100m, 102m, 99m, 101m));  // 5: Exit fill

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(101m) },
            new BacktestRiskManagementSettings
            {
                StopLossPercent = 0.15m,
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrStop = new AtrExitParameters { Period = 2, Multiple = 1.0m },
                    TrailingStop = new TrailingExitParameters { Mode = TrailingExitMode.Ratio, Ratio = 0.05m },
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);
        var trade = Assert.Single(result.Trades);

        Assert.Equal("Trailing Stop", trade.ExitReason);
        Assert.Equal(104.5m, trade.StopLevelAtExit);
    }

    [Fact]
    public void TrailingStop_WithTakeProfit_StillRatchetsOnEveryBar()
    {
        // Proof obligation for HIGH-01: When TakeProfit and TrailingStop coexist,
        // TrailingStop must continue to ratchet upward on non-breaching bars even though
        // winner == SlTpWinner.None in EvaluateRiskManagementBreach.
        // Bar 0: O=100, H=101, L=99, C=100
        // Bar 1: O=100, H=106, L=99, C=105 -> Signal (C > 102)
        // Bar 2: O=100, H=110, L=99, C=108 -> Entry at 100. High=110. Initial ratchet = 110 * 0.90 = 99m.
        // Bar 3: O=108, H=120, L=105, C=118 -> Active stop is 99m. High 120 < 150 (TP at 150 not breached).
        //        Ratchets to max(99, 120 * 0.90) = 108m.
        // Bar 4: O=115, H=116, L=106, C=107 -> Active stop is 108m. Low 106 <= 108 -> Trailing Stop BREACH!
        // Bar 5: O=104, H=106, L=102, C=103 -> Fills at Open 104.
        var bars = SyntheticBars.FromTable(
            (100m, 101m, 99m, 100m),   // 0
            (100m, 106m, 99m, 105m),   // 1: Signal
            (100m, 110m, 99m, 108m),   // 2: Entry at 100
            (108m, 120m, 105m, 118m),  // 3: High=120 -> Ratchets stop to 108m
            (115m, 116m, 106m, 107m),  // 4: Low=106 <= 108 -> Breach!
            (104m, 106m, 102m, 103m)); // 5: Fill at 104

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(102m) },
            new BacktestRiskManagementSettings
            {
                TakeProfitPercent = 0.50m, // Target at 150m (far away)
                DynamicExits = new DynamicExitConfiguration
                {
                    TrailingStop = new TrailingExitParameters
                    {
                        Mode = TrailingExitMode.Ratio,
                        Ratio = 0.10m,
                    },
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);
        var trade = Assert.Single(result.Trades);

        Assert.Equal(2, trade.EntryBar);
        Assert.Equal(5, trade.ExitBar);
        Assert.Equal("Trailing Stop", trade.ExitReason);
        Assert.Equal(108.0m, trade.StopLevelAtExit);
    }

    [Fact]
    public void AtrStop_WhenMultipleTimesAtrExceedsEntryPrice_DoesNotThrowOverflow()
    {
        // Proof obligation for HIGH-03: When Multiple * ATR >= entryPrice (e.g. 2.0 * 60 = 120 >= 100),
        // stopPrice <= 0. Strategy must treat non-positive stop as null rather than throwing OverflowException.
        // Bar 0, 1: ATR(2) = 60.0m
        // Bar 2: Signal
        // Bar 3: Entry at 100
        var bars = SyntheticBars.FromTable(
            (100m, 160m, 100m, 130m),  // 0: TR = 60
            (130m, 160m, 100m, 130m),  // 1: TR = 60 -> ATR(2) = 60.0m
            (130m, 135m, 125m, 132m),  // 2: Signal (C > 130)
            (100m, 105m, 95m, 101m),   // 3: Entry at 100. Multiple=2.0 -> Multiple * ATR = 120 >= 100.
            (100m, 105m, 95m, 101m),   // 4: Holding
            (90m, 95m, 85m, 88m));     // 5

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(130m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrStop = new AtrExitParameters { Period = 2, Multiple = 2.0m },
                },
            });

        // Must run cleanly without throwing OverflowException
        var result = VerificationHarness.Run(input, config, strategy);
        Assert.NotNull(result);
    }

    [Fact]
    public void MutationTest_TrailingStopBarHigh_OnlyAffectsBarTPlus1Onward()
    {
        // Proof obligation (c)-part-2: Mutating bar t's high has zero effect on bar t's active stop;
        // it only updates the ratcheted stop for bar t+1 onward.
        // Run A (unmutated): Bar 3 High is 110m.
        // Run B (mutated): Bar 3 High is 160m.
        // In both runs:
        // Bar 2: Entry at 100. Stop for Bar 3 = 100 * 0.90 = 90m.
        // Bar 3: Active stop is 90m in both runs. Low is 95m > 90m -> neither run breaches on Bar 3!
        // Bar 4:
        //   In Run A: Active stop is max(90, 110 * 0.90) = 99m. Low is 135m > 99m -> NO breach!
        //   In Run B: Active stop is max(90, 160 * 0.90) = 144m. Low is 135m <= 144m -> BREACH on Bar 4!
        var barsA = SyntheticBars.FromTable(
            (100m, 101m, 99m, 100m),   // 0
            (100m, 106m, 99m, 105m),   // 1: Signal
            (100m, 102m, 98m, 100m),   // 2: Entry at 100. Stop for Bar 3 = 90m.
            (100m, 110m, 95m, 105m),   // 3: Low=95 > 90 (no breach). High=110 -> Stop for Bar 4 = 99m.
            (136m, 140m, 135m, 138m),  // 4: Active stop 99m. Low=135 > 99 -> no breach.
            (138m, 140m, 90m, 95m),    // 5: Active stop = max(99, 140 * 0.90) = 126m (Bar 4 High=140 ratchets it). Low=90 <= 126 -> Breach!
            (95m, 96m, 94m, 95m));     // 6: Exit fill

        var barsB = SyntheticBars.FromTable(
            (100m, 101m, 99m, 100m),   // 0
            (100m, 106m, 99m, 105m),   // 1: Signal
            (100m, 102m, 98m, 100m),   // 2: Entry at 100. Stop for Bar 3 = 90m.
            (100m, 160m, 95m, 105m),   // 3: Low=95 > 90 (no breach despite High=160!). High=160 -> Stop for Bar 4 = 144m.
            (136m, 140m, 135m, 138m),  // 4: Active stop 144m. Low=135 <= 144 -> Breach!
            (130m, 132m, 128m, 130m)); // 5: Exit fill

        var config = DefaultConfig();
        var strategyA = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(102m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    TrailingStop = new TrailingExitParameters { Mode = TrailingExitMode.Ratio, Ratio = 0.10m },
                },
            });
        var strategyB = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(102m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    TrailingStop = new TrailingExitParameters { Mode = TrailingExitMode.Ratio, Ratio = 0.10m },
                },
            });

        var resultA = VerificationHarness.Run(VerificationHarness.MakeInput(barsA), config, strategyA);
        var resultB = VerificationHarness.Run(VerificationHarness.MakeInput(barsB), config, strategyB);

        var tradeA = Assert.Single(resultA.Trades);
        var tradeB = Assert.Single(resultB.Trades);

        Assert.Equal(6, tradeA.ExitBar); // Exited on Bar 6
        Assert.Equal(5, tradeB.ExitBar); // Exited on Bar 5
        Assert.Equal(126.0m, tradeA.StopLevelAtExit);
        Assert.Equal(144.0m, tradeB.StopLevelAtExit);
    }

    [Fact]
    public void SameBarDoubleBreach_DynamicStopWinsOverTakeProfitAndAtrTarget()
    {
        // Proof obligation (d): When both dynamic stop and profit target are breached within the same bar,
        // the stop-loss side wins (conservative risk evaluation invariant).
        // Bar 0, 1, 2: ATR(2) = 10.0m
        // Bar 3: Entry at 100. ATR Stop = 90.0m. TakeProfit = 110.0m.
        // Bar 4: O=100, H=115, L=85, C=100 -> Low 85 <= 90 (Stop breach) AND High 115 >= 110 (TP breach).
        // Bar 5: Fill at Open 92.
        var bars = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 105m, 95m, 100m),   // 1
            (100m, 105m, 95m, 102m),   // 2: Signal (ATR=10)
            (100m, 102m, 99m, 101m),   // 3: Entry at 100
            (100m, 115m, 85m, 100m),   // 4: Double breach: H=115 >= 110 and L=85 <= 90
            (92m, 95m, 90m, 91m));     // 5: Exit fill

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(101m) },
            new BacktestRiskManagementSettings
            {
                TakeProfitPercent = 0.10m,
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrStop = new AtrExitParameters { Period = 2, Multiple = 1.0m }, // Stop at 90.0m
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);
        var trade = Assert.Single(result.Trades);

        Assert.Equal("ATR Stop", trade.ExitReason);
        Assert.Equal(90.0m, trade.StopLevelAtExit);
    }

    [Fact]
    public void ExactPriceTie_FixedStopLossRetainsReasonOverDynamicStop()
    {
        // MED-02 / Strict inequality: When Fixed SL and Dynamic Stop have identical prices (e.g. both 90.0m),
        // Fixed Stop-Loss retains priority on exact tie; reason must be "Stop-Loss breach".
        // Entry at 100m.
        // StopLossPercent = 0.10m -> 90.0m.
        // ATR Stop: ATR(2) = 10.0m, Multiple = 1.0m -> 90.0m.
        var bars = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 105m, 95m, 100m),   // 1
            (100m, 105m, 95m, 102m),   // 2: Signal
            (100m, 102m, 99m, 101m),   // 3: Entry at 100
            (95m, 96m, 88m, 89m),      // 4: Low=88 <= 90.0m -> Breach
            (88m, 90m, 85m, 87m));     // 5: Exit fill

        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var strategy = new ConditionBasedBacktestStrategy(
            new[] { CloseAbove(101m) },
            new BacktestRiskManagementSettings
            {
                StopLossPercent = 0.10m,
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrStop = new AtrExitParameters { Period = 2, Multiple = 1.0m },
                },
            });

        var result = VerificationHarness.Run(input, config, strategy);
        var trade = Assert.Single(result.Trades);

        Assert.Equal("Stop-Loss breach", trade.ExitReason);
        Assert.Equal(90.0m, trade.StopLevelAtExit);
    }

    [Fact]
    public void ShortPosition_AtrStop_AtrTarget_And_TrailingStop_TriggerCorrectly()
    {
        // Parity test for Short positions across dynamic exit types.
        var config = DefaultConfig();

        // 1. Short ATR Stop: Entry at 100, ATR=10, stop at 110. High reaches 112 -> Breach!
        var barsAtrStop = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 105m, 95m, 100m),   // 1: TR=10
            (100m, 105m, 95m, 98m),    // 2: TR=10 -> ATR(2)=10. Signal (C < 99)
            (100m, 101m, 98m, 99m),    // 3: Short Entry at 100. Stop at 110.
            (100m, 112m, 99m, 105m),   // 4: High=112 >= 110 -> Breach!
            (111m, 113m, 109m, 110m)); // 5: Exit fill

        var stratAtrStop = new ConditionBasedBacktestStrategy(
            new[] { CloseBelow(99m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrStop = new AtrExitParameters { Period = 2, Multiple = 1.0m },
                },
            });
        var resAtrStop = VerificationHarness.Run(VerificationHarness.MakeInput(barsAtrStop), config, stratAtrStop);
        var tradeAtrStop = Assert.Single(resAtrStop.Trades);
        Assert.Equal(TradeSide.Short, tradeAtrStop.Side);
        Assert.Equal("ATR Stop", tradeAtrStop.ExitReason);
        Assert.Equal(110.0m, tradeAtrStop.StopLevelAtExit);

        // 2. Short ATR Target: Entry at 100, ATR=10, target at 90. Low reaches 88 -> Breach!
        var barsAtrTarget = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 105m, 95m, 100m),   // 1
            (100m, 105m, 95m, 98m),    // 2: Signal
            (100m, 101m, 98m, 99m),    // 3: Short Entry at 100. Target at 90.
            (99m, 101m, 88m, 92m),     // 4: Low=88 <= 90 -> Breach!
            (89m, 91m, 87m, 88m));     // 5: Exit fill

        var stratAtrTarget = new ConditionBasedBacktestStrategy(
            new[] { CloseBelow(99m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    AtrTarget = new AtrExitParameters { Period = 2, Multiple = 1.0m },
                },
            });
        var resAtrTarget = VerificationHarness.Run(VerificationHarness.MakeInput(barsAtrTarget), config, stratAtrTarget);
        var tradeAtrTarget = Assert.Single(resAtrTarget.Trades);
        Assert.Equal(TradeSide.Short, tradeAtrTarget.Side);
        Assert.Equal("ATR Target", tradeAtrTarget.ExitReason);
        Assert.Null(tradeAtrTarget.StopLevelAtExit);

        // 3. Short Trailing Stop: Entry at 100. Low ratchets down. High breaches ratcheted stop.
        // Bar 2: Signal (C < 99)
        // Bar 3: Entry at 100. Low = 90. Ratcheted stop for Bar 4 = 90 + 90 * 0.10 = 99m.
        // Bar 4: Low = 80. Active stop = 99m. High = 95 < 99 (no breach). Ratcheted stop for Bar 5 = min(99, 80 + 8) = 88m.
        // Bar 5: Active stop = 88m. High = 91 >= 88 -> BREACH!
        // Bar 6: Exit fill at Open 92.
        var barsTrailing = SyntheticBars.FromTable(
            (100m, 100m, 100m, 100m),  // 0
            (100m, 101m, 99m, 100m),   // 1
            (100m, 101m, 97m, 98m),    // 2: Signal
            (100m, 101m, 90m, 92m),    // 3: Short Entry at 100. L=90 -> Stop for Bar 4 = 99m.
            (92m, 95m, 80m, 82m),      // 4: Active stop 99m. High=95 < 99. L=80 -> Stop for Bar 5 = 88m.
            (85m, 91m, 84m, 86m),      // 5: Active stop 88m. High=91 >= 88 -> Breach!
            (92m, 94m, 89m, 90m));     // 6: Exit fill

        var stratTrailing = new ConditionBasedBacktestStrategy(
            new[] { CloseBelow(99m) },
            new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    TrailingStop = new TrailingExitParameters { Mode = TrailingExitMode.Ratio, Ratio = 0.10m },
                },
            });
        var resTrailing = VerificationHarness.Run(VerificationHarness.MakeInput(barsTrailing), config, stratTrailing);
        var tradeTrailing = Assert.Single(resTrailing.Trades);
        Assert.Equal(TradeSide.Short, tradeTrailing.Side);
        Assert.Equal("Trailing Stop", tradeTrailing.ExitReason);
        Assert.Equal(88.0m, tradeTrailing.StopLevelAtExit);
    }

    [Fact]
    public void DynamicExitConfiguration_CrossModeValidation_And_DisabledNormalization()
    {
        // 1. Cross-mode parameter rejections in BacktestConditionValidator:
        Assert.Throws<ArgumentException>(() =>
            BacktestConditionValidator.ValidateRiskManagement(new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    TrailingStop = new TrailingExitParameters
                    {
                        Mode = TrailingExitMode.AtrMultiple,
                        Period = 14,
                        Multiple = 2.0m,
                        Ratio = 0.05m, // Disallowed in AtrMultiple mode
                    },
                },
            }));

        Assert.Throws<ArgumentException>(() =>
            BacktestConditionValidator.ValidateRiskManagement(new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    TrailingStop = new TrailingExitParameters
                    {
                        Mode = TrailingExitMode.Ratio,
                        Ratio = 0.05m,
                        Period = 14, // Disallowed in Ratio mode
                    },
                },
            }));

        Assert.Throws<ArgumentException>(() =>
            BacktestConditionValidator.ValidateRiskManagement(new BacktestRiskManagementSettings
            {
                DynamicExits = new DynamicExitConfiguration
                {
                    TrailingStop = new TrailingExitParameters
                    {
                        Mode = TrailingExitMode.Ratio,
                        Ratio = 0.05m,
                        Multiple = 2.0m, // Disallowed in Ratio mode
                    },
                },
            }));

        // 2. Disabled DynamicExitConfiguration normalizes to null and produces identical fingerprint:
        var emptyConfig = new DynamicExitConfiguration();
        Assert.False(emptyConfig.IsEnabled);

        var bars = SyntheticBars.FromTable(
            (100m, 101m, 99m, 100m),
            (100m, 106m, 99m, 105m),
            (100m, 105m, 98m, 102m),
            (100m, 103m, 97m, 99m));
        var input = VerificationHarness.MakeInput(bars);
        var config = DefaultConfig();

        var stratNull = new ConditionBasedBacktestStrategy(new[] { CloseAbove(102m) }, new BacktestRiskManagementSettings { DynamicExits = null });
        var stratEmpty = new ConditionBasedBacktestStrategy(new[] { CloseAbove(102m) }, new BacktestRiskManagementSettings { DynamicExits = emptyConfig });

        var resNull = VerificationHarness.Run(input, config, stratNull);
        var resEmpty = VerificationHarness.Run(input, config, stratEmpty);
        var fpNull = BacktestRunFingerprintBuilder.Freeze(input, config, stratNull).Seal(resNull);
        var fpEmpty = BacktestRunFingerprintBuilder.Freeze(input, config, stratEmpty).Seal(resEmpty);

        Assert.Equal(fpNull.ToHexString(), fpEmpty.ToHexString());
    }
}