using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class CorporateActionDetectorTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CandleData MakeBar(int dayOffset, decimal close, long volume)
    {
        return new CandleData(
            Timestamp: BaseTime.AddDays(dayOffset),
            Open: close,
            High: close * 1.01m,
            Low: close * 0.99m,
            Close: close,
            Volume: volume);
    }

    [Fact]
    public void DetectPotentialSplits_EmptyOrSingleBar_ReturnsEmpty()
    {
        var empty = ImmutableArray<CandleData>.Empty;
        var warningsEmpty = CorporateActionDetector.DetectPotentialSplits(empty);
        Assert.Empty(warningsEmpty);

        var single = ImmutableArray.Create(MakeBar(0, 100m, 10_000));
        var warningsSingle = CorporateActionDetector.DetectPotentialSplits(single);
        Assert.Empty(warningsSingle);
    }

    [Fact]
    public void DetectPotentialSplits_StandardTwoToOneForwardSplit_EmitsWarning()
    {
        // 5 baseline bars at $100 with volume 10,000
        // 1 split bar at $50 (ratio 2.0) with volume 30,000 (3.0x surge >= 2.5x)
        var builder = ImmutableArray.CreateBuilder<CandleData>();
        for (int i = 0; i < 5; i++)
        {
            builder.Add(MakeBar(i, 100m, 10_000));
        }
        builder.Add(MakeBar(5, 50m, 30_000));

        var warnings = CorporateActionDetector.DetectPotentialSplits(builder.ToImmutable());

        Assert.Single(warnings);
        BacktestWarning warning = warnings[0];
        Assert.Equal(WarningCode.PotentialUnadjustedSplit, warning.Code);
        Assert.Equal(5, warning.BarIndex);
        Assert.Equal(BaseTime.AddDays(5), warning.Timestamp);
        Assert.Contains("split detected at bar 5", warning.Message);
        Assert.Contains("2.00", warning.Message);
    }

    [Fact]
    public void DetectPotentialSplits_StandardOneToTwoReverseSplit_EmitsWarning()
    {
        // 5 baseline bars at $50 with volume 10,000
        // 1 reverse split bar at $100 (ratio 0.5) with volume 35,000 (3.5x surge >= 2.5x)
        var builder = ImmutableArray.CreateBuilder<CandleData>();
        for (int i = 0; i < 5; i++)
        {
            builder.Add(MakeBar(i, 50m, 10_000));
        }
        builder.Add(MakeBar(5, 100m, 35_000));

        var warnings = CorporateActionDetector.DetectPotentialSplits(builder.ToImmutable());

        Assert.Single(warnings);
        BacktestWarning warning = warnings[0];
        Assert.Equal(WarningCode.PotentialUnadjustedSplit, warning.Code);
        Assert.Equal(5, warning.BarIndex);
        Assert.Equal(BaseTime.AddDays(5), warning.Timestamp);
        Assert.Contains("split detected at bar 5", warning.Message);
        Assert.Contains("0.50", warning.Message);
    }

    [Fact]
    public void DetectPotentialSplits_HighVolatilityWithoutVolumeSurge_ReturnsEmpty()
    {
        // 5 baseline bars at $100 with volume 10,000
        // 1 drop bar to $50 (50% drop), but volume is only 12,000 (1.2x surge < 2.5x)
        var builder = ImmutableArray.CreateBuilder<CandleData>();
        for (int i = 0; i < 5; i++)
        {
            builder.Add(MakeBar(i, 100m, 10_000));
        }
        builder.Add(MakeBar(5, 50m, 12_000));

        var warnings = CorporateActionDetector.DetectPotentialSplits(builder.ToImmutable());

        Assert.Empty(warnings);
    }

    [Fact]
    public void DetectPotentialSplits_VolumeSurgeWithNonSplitRatio_ReturnsEmpty()
    {
        // 5 baseline bars at $100 with volume 10,000
        // 1 drop bar to $71.4m (~28.6% drop), volume surges 4x (40,000)
        // Ratio is 100 / 71.4 = 1.40, which does NOT match standard split ratios within 5%
        var builder = ImmutableArray.CreateBuilder<CandleData>();
        for (int i = 0; i < 5; i++)
        {
            builder.Add(MakeBar(i, 100m, 10_000));
        }
        builder.Add(MakeBar(5, 71.4m, 40_000));

        var warnings = CorporateActionDetector.DetectPotentialSplits(builder.ToImmutable());

        Assert.Empty(warnings);
    }

    [Fact]
    public void DetectPotentialSplits_ZeroOrNegativeValues_DoesNotThrow()
    {
        var bars = ImmutableArray.Create(
            MakeBar(0, 0m, 0),
            MakeBar(1, -10m, -5),
            MakeBar(2, 50m, 10_000));

        var warnings = CorporateActionDetector.DetectPotentialSplits(bars);
        Assert.Empty(warnings);
    }

    [Fact]
    public void BacktestEngine_Run_IntegratesCorporateActionWarnings()
    {
        var barsBuilder = ImmutableArray.CreateBuilder<CandleData>();
        for (int i = 0; i < 5; i++)
        {
            barsBuilder.Add(MakeBar(i, 100m, 10_000));
        }
        barsBuilder.Add(MakeBar(5, 50m, 30_000)); // 2:1 split with volume surge
        barsBuilder.Add(MakeBar(6, 50m, 10_000));

        var bars = barsBuilder.ToImmutable();
        var input = new BacktestInput(
            bars,
            "SPLIT_TEST",
            TimeFrame.D1,
            BacktestInput.CurrentDataVersion,
            bars[0].Timestamp,
            bars[^1].Timestamp.AddDays(1),
            historyStartIndex: 0,
            tradingStartIndex: 0);

        var config = new BacktestConfiguration
        {
            InitialCapital = 10_000m,
            CommissionFlat = 0m,
            CommissionPerUnit = 0m,
            SlippageRatio = 0m,
            SizingModel = PositionSizingModel.FixedQuantity,
            SizingParameter = 10m,
        };

        var emptyStrategy = new NullStrategy();
        var engine = new BacktestEngine(new StockAnalyzer.Core.Models.Indicators.IndicatorFactory());

        var result = engine.Run(input, config, emptyStrategy, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Warnings);
        Assert.Equal(WarningCode.PotentialUnadjustedSplit, result.Warnings[0].Code);
        Assert.Equal(5, result.Warnings[0].BarIndex);
    }

    [Fact]
    public void DetectPotentialSplits_CultureInvariance_FormatsNumbersWithPeriodRegardlessOfCulture()
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>();
        for (int i = 0; i < 5; i++)
        {
            builder.Add(MakeBar(i, 100m, 10_000));
        }
        builder.Add(MakeBar(5, 50m, 30_000));

        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var warnings = CorporateActionDetector.DetectPotentialSplits(builder.ToImmutable());
            Assert.Single(warnings);
            Assert.Contains("2.00", warnings[0].Message);
            Assert.Contains("3.0x", warnings[0].Message);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void CorporateActionHandling_AbortOnDetection_ThrowsDataIntegrityException()
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>();
        for (int i = 0; i < 5; i++)
        {
            builder.Add(MakeBar(i, 100m, 10_000));
        }
        builder.Add(MakeBar(5, 50m, 30_000));
        var bars = builder.ToImmutable();

        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, BaseTime, BaseTime.AddDays(10), 0, 0);
        var config = new BacktestConfiguration
        {
            InitialCapital = 100_000m,
            SizingParameter = 1m,
            CorporateActionHandlingMode = CorporateActionHandlingMode.AbortOnDetection,
        };

        var engine = new BacktestEngine(new StockAnalyzer.Core.Models.Indicators.IndicatorFactory());
        Action runAction = () => engine.Run(input, config, new NullStrategy());
        Assert.Throws<DataIntegrityException>(runAction);
    }

    [Fact]
    public void CorporateActionHandling_AutoAdjustHistorical_AdjustsBarsAndClearsWarnings()
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>();
        for (int i = 0; i < 5; i++)
        {
            builder.Add(MakeBar(i, 100m, 10_000));
        }
        builder.Add(MakeBar(5, 50m, 30_000));
        var bars = builder.ToImmutable();

        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, BaseTime, BaseTime.AddDays(10), 0, 0);
        var config = new BacktestConfiguration
        {
            InitialCapital = 100_000m,
            SizingParameter = 1m,
            CorporateActionHandlingMode = CorporateActionHandlingMode.AutoAdjustHistorical,
        };

        var engine = new BacktestEngine(new StockAnalyzer.Core.Models.Indicators.IndicatorFactory());
        BacktestResult result = engine.Run(input, config, new NullStrategy());

        Assert.Equal(RunStatus.Completed, result.Status);
        // After backward adjustment (100 -> 50), the price ratio 50/50 is 1.0 (no jump), so no warning
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AdjustSplits_BackwardAdjustment_PreservesSpreadAndAdjustsVolume()
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>();
        for (int i = 0; i < 5; i++)
        {
            builder.Add(MakeBar(i, 100m, 10_000));
        }
        builder.Add(MakeBar(5, 50m, 30_000));
        var bars = builder.ToImmutable();

        var warnings = CorporateActionDetector.DetectPotentialSplits(bars);
        Assert.Single(warnings);

        var adjusted = CorporateActionDetector.AdjustSplits(bars, warnings);

        Assert.Equal(bars.Length, adjusted.Length);
        // Prior bars (0 to 4) should have close adjusted to 100 / 2 = 50, and volume to 10,000 * 2 = 20,000
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(50m, adjusted[i].Close);
            Assert.Equal(20_000, adjusted[i].Volume);
        }
        // Split bar (5) should remain untouched (50 close, 30,000 volume)
        Assert.Equal(50m, adjusted[5].Close);
        Assert.Equal(30_000, adjusted[5].Volume);
    }

    private sealed class NullStrategy : IBacktestStrategy
    {
        public string Name => "Null";
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => Array.Empty<StrategyIndicatorRequest>();
        public StrategyOrderRequest? Evaluate(StrategyContext context) => null;
    }
}
