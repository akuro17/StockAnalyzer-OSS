using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Rendering;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// Output contract of the chart-pattern flag indicators (plan ChartPatterns56, S-1, S-2, S-3 model W, S-6). The contract is proved on probe indicators that use the real base class
/// (they only differ in the one rule they evaluate), and, as rows are added to <see cref="PatternFlagIndicatorCatalog"/>, on every registered type.
/// </summary>
public class PatternFlagContractTests
{
    private const int BarCount = 400;
    private const int WindowBars = ChartConstants.PatternDetectionWindowBars;

    /// <summary>A probe full-series indicator that compares the close at t against the close up to WindowBars earlier.</summary>
    private sealed class ProbeIndicator : CorePatternFlagFullSeriesIndicatorBase
    {
        private readonly PatternDirectionMode _mode;
        private readonly Func<IReadOnlyList<CandleData>, int, PatternFlagOutcome> _rule;

        public ProbeIndicator(PatternFlagSpec spec, PatternDirectionMode mode = PatternDirectionMode.Both, Func<IReadOnlyList<CandleData>, int, PatternFlagOutcome>? rule = null)
            : base(spec)
        {
            _mode = mode;
            _rule = rule ?? DefaultRule(spec.HasDirection);
        }

        protected override PatternDirectionMode DirectionMode => _mode;

        protected override void FillSeries(CandleData[] bars, int count, int minBars, decimal?[] main, decimal?[]? direction, decimal?[]? bullish, decimal?[]? bearish, CancellationToken cancellationToken)
        {
            for (int t = 0; t < count; t++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (t + 1 < minBars)
                {
                    continue;
                }

                WriteOutcome(ApplyDirectionMode(_rule(bars, t)), t, main, direction, bullish, bearish);
            }
        }

        public IIndicatorResult CalculateWith(IReadOnlyList<CoreCandleData> candles, CancellationToken token) => CalculateGuarded(candles, token);

        private static Func<IReadOnlyList<CandleData>, int, PatternFlagOutcome> DefaultRule(bool directional) => (bars, t) =>
        {
            int start = Math.Max(0, t - WindowBars + 1);
            if (t - start + 1 < 2)
            {
                return PatternFlagOutcome.None;
            }

            decimal first = bars[start].Close;
            decimal last = bars[t].Close;
            if (!directional)
            {
                return last > first ? PatternFlagOutcome.Met : PatternFlagOutcome.None;
            }

            return last > first ? PatternFlagOutcome.Bullish : last < first ? PatternFlagOutcome.Bearish : PatternFlagOutcome.None;
        };
    }

    private static PatternFlagSpec Spec(bool hasDirection, int minBars = 3) => new(
        IndicatorType.DarvasBoxSignal,
        CoreIndicatorCategory.PatternRecognition,
        minBars,
        PatternFlagEvaluationModel.FullSeries,
        hasDirection,
        IndicatorDefaultConstants.PatternRecognitionFlagColor,
        typeof(CoreDarvasBoxSignalParameter),
        BullishSeries: hasDirection ? "ProbeUp" : null,
        BearishSeries: hasDirection ? "ProbeDown" : null);

    private static CoreCandleData[] Candles(int seed = VerificationParameters.PrimarySeed, int count = BarCount)
        => SyntheticBars.SeededRandomWalk(seed, count, VerificationParameters.FuzzStartPrice)
            .Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume))
            .ToArray();

    private static IReadOnlyList<decimal?> Series(IIndicatorResult result, string name)
    {
        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.True(result.HasSeries(name), $"series '{name}' is missing");
        return result.GetSeries(name);
    }

    // ---------------------------------------------------------------- S-2 input boundaries

    [Fact]
    public void NullCandles_GiveTheBaseClassFailure_NotAnException()
    {
        var result = new ProbeIndicator(Spec(false)).Calculate(null!);

        Assert.False(result.IsSuccessful);
        Assert.Equal("Candles data is null.", result.ErrorMessage);
    }

    [Fact]
    public void EmptyCandles_GiveAnEmptyResult()
    {
        var indicator = new ProbeIndicator(Spec(true));

        var result = indicator.Calculate(Array.Empty<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(indicator.Values);
        Assert.False(result.HasSeries(IndicatorResult.MainSeriesName));
    }

    [Fact]
    public void FewerBarsThanMinBars_GiveAllNullSeriesOfTheInputLength()
    {
        var indicator = new ProbeIndicator(Spec(true, minBars: 50));
        var candles = Candles(count: 49);

        var result = indicator.Calculate(candles);

        Assert.Equal(49, Series(result, IndicatorResult.MainSeriesName).Count);
        Assert.All(Series(result, IndicatorResult.MainSeriesName), v => Assert.Null(v));
        Assert.All(Series(result, IndicatorSeriesNames.PatternDirection), v => Assert.Null(v));
    }

    [Fact]
    public void ABarWithAZeroPrice_DoesNotFailTheSeries()
    {
        var candles = Candles(count: 120);
        candles[60] = candles[60] with { Open = 0m, High = 0m, Low = 0m, Close = 0m };

        var result = new ProbeIndicator(Spec(true)).Calculate(candles);

        Assert.True(result.IsSuccessful, result.ErrorMessage);
        Assert.Equal(candles.Length, result.GetSeries(IndicatorResult.MainSeriesName).Count);
    }

    [Fact]
    public void ACancelledToken_StopsTheLoop()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new ProbeIndicator(Spec(false)).CalculateWith(Candles(count: 60), cts.Token));
    }

    // ---------------------------------------------------------------- S-1 output contract

    [Fact]
    public void MainAndDirection_FollowTheSeriesDomainsAndInvariants()
    {
        var indicator = new ProbeIndicator(Spec(true, minBars: 5));
        var candles = Candles();

        var result = indicator.Calculate(candles);
        var main = Series(result, IndicatorResult.MainSeriesName);
        var direction = Series(result, IndicatorSeriesNames.PatternDirection);

        Assert.Equal(candles.Length, main.Count);
        Assert.Equal(candles.Length, direction.Count);
        Assert.False(indicator.IsOverlay);
        Assert.Equal(main, indicator.Values);

        for (int t = 0; t < candles.Length; t++)
        {
            if (t + 1 < 5)
            {
                Assert.Null(main[t]);
                Assert.Null(direction[t]);
                continue;
            }

            Assert.Contains(main[t], new decimal?[] { 0m, 1m });
            Assert.Contains(direction[t], new decimal?[] { -1m, 0m, 1m });
            if (main[t] == 0m)
            {
                Assert.Equal(0m, direction[t]);
            }
            else
            {
                Assert.NotEqual(0m, direction[t]);
            }
        }

        Assert.Contains(main, v => v == 1m);
        Assert.Contains(main, v => v == 0m);
        Assert.Contains(direction, v => v == 1m);
        Assert.Contains(direction, v => v == -1m);
    }

    [Fact]
    public void AnUndirectedType_HasNoDirectionSeries()
    {
        var result = new ProbeIndicator(Spec(false)).Calculate(Candles(count: 60));

        Assert.True(result.HasSeries(IndicatorResult.MainSeriesName));
        Assert.False(result.HasSeries(IndicatorSeriesNames.PatternDirection));
    }

    [Fact]
    public void ABidirectionalMatchWithoutADirection_IsAFailureResult()
    {
        var indicator = new ProbeIndicator(Spec(true), rule: (_, _) => PatternFlagOutcome.Met);

        var result = indicator.Calculate(Candles(count: 30));

        Assert.False(result.IsSuccessful);
    }

    [Theory]
    [InlineData(PatternDirectionMode.BullishOnly, 1)]
    [InlineData(PatternDirectionMode.BearishOnly, -1)]
    public void DirectionMode_NarrowsMainAndDirectionTogether(PatternDirectionMode mode, int allowedDirection)
    {
        var candles = Candles();
        var all = new ProbeIndicator(Spec(true)).Calculate(candles);
        var narrowed = new ProbeIndicator(Spec(true), mode).Calculate(candles);

        var allMain = Series(all, IndicatorResult.MainSeriesName);
        var allDirection = Series(all, IndicatorSeriesNames.PatternDirection);
        var main = Series(narrowed, IndicatorResult.MainSeriesName);
        var direction = Series(narrowed, IndicatorSeriesNames.PatternDirection);

        for (int t = 0; t < candles.Length; t++)
        {
            if (allMain[t] is null)
            {
                Assert.Null(main[t]);
                continue;
            }

            bool allowed = allDirection[t] == allowedDirection;
            Assert.Equal(allowed ? 1m : 0m, main[t]);
            Assert.Equal(allowed ? allowedDirection : 0m, direction[t]);
        }
    }

    // ---------------------------------------------------------------- S-3 model W: window locality

    [Fact]
    public void WindowLocality_TheValueAtABarEqualsTheValueOfTheIsolatedWindow()
    {
        var candles = Candles();
        var full = Series(new ProbeIndicator(Spec(true)).Calculate(candles), IndicatorResult.MainSeriesName);
        var fullDirection = Series(new ProbeIndicator(Spec(true)).Calculate(candles), IndicatorSeriesNames.PatternDirection);

        foreach (int t in new[] { 2, 30, WindowBars - 1, WindowBars, WindowBars + 1, 250, candles.Length - 1 })
        {
            int start = Math.Max(0, t - WindowBars + 1);
            var slice = candles.Skip(start).Take(t - start + 1).ToArray();

            var isolated = new ProbeIndicator(Spec(true)).Calculate(slice);

            Assert.Equal(full[t], isolated.GetSeries(IndicatorResult.MainSeriesName)[^1]);
            Assert.Equal(fullDirection[t], isolated.GetSeries(IndicatorSeriesNames.PatternDirection)[^1]);
        }
    }

    [Fact]
    public void PrefixInvariance_AppendingBarsNeverChangesAnEarlierValue()
    {
        var candles = Candles();
        var full = Series(new ProbeIndicator(Spec(true)).Calculate(candles), IndicatorResult.MainSeriesName);
        var prefix = Series(new ProbeIndicator(Spec(true)).Calculate(candles.Take(250).ToArray()), IndicatorResult.MainSeriesName);

        for (int t = 0; t < prefix.Count; t++)
        {
            Assert.Equal(full[t], prefix[t]);
        }
    }

    // ---------------------------------------------------------------- constants and defaults

    [Fact]
    public void TheWindowConstant_IsTheOneValueOfTheScreenerCriteriaAndOfTheFlagIndicators()
    {
        Assert.Equal(100, ChartConstants.PatternDetectionWindowBars);
        Assert.Equal(ChartConstants.PatternDetectionWindowBars, ScreenerHistoryRequirements.CriteriaWindowBars);
        Assert.Equal(ScreenerHistoryRequirements.CriteriaWindowBars, ScreenerHistoryRequirements.HistoryMarginBars);
    }

    [Fact]
    public void DefaultSettings_OfAFlag_AreADisabledSubWindowWithTheFixedYRange()
    {
        PatternFlagSpec spec = Spec(true);

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.CreatePatternFlag(spec);

        Assert.Equal(spec.Type, settings.TypeEnum);
        Assert.Equal(CoreIndicatorCategory.PatternRecognition, settings.Category);
        Assert.False(settings.IsEnabled);
        Assert.False(settings.IsOverlay);
        Assert.Equal(-1.2m, settings.MinValue); // a bidirectional flag also plots -1m (the bearish side)
        Assert.Equal(1.2m, settings.MaxValue);
        Assert.Equal(spec.Color, settings.Color);
        Assert.Null(settings.MainRenderPrimitive);
        Assert.IsType<CoreDarvasBoxSignalParameter>(settings.ParameterObject);
    }

    [Fact]
    public void ProbeIndicator_UsesTheCatalogSpecForItsDefaultSettings()
    {
        PatternFlagSpec spec = Spec(false);

        CoreIndicatorSettings settings = new ProbeIndicator(spec).GetDefaultSettings();

        Assert.Equal(spec.Category, settings.Category);
        Assert.False(settings.IsOverlay);
        Assert.Equal(IndicatorDefaultConstants.PatternFlagMaxValue, settings.MaxValue);
    }

    // ---------------------------------------------------------------- every registered type (vacuous while the catalog is empty)

    [Fact]
    public void EveryCatalogRow_IsConsistentWithItsRegisteredIndicator()
    {
        var factory = new IndicatorFactory();
        var seen = new HashSet<IndicatorType>();
        var newCategories = new HashSet<CoreIndicatorCategory>
        {
            CoreIndicatorCategory.CandlestickPatterns, CoreIndicatorCategory.Harmonic, CoreIndicatorCategory.MarketStructure,
            CoreIndicatorCategory.PatternRecognition, CoreIndicatorCategory.TradingRules, CoreIndicatorCategory.WaveTheory,
        };

        foreach (PatternFlagSpec row in PatternFlagIndicatorCatalog.Rows)
        {
            Assert.True(seen.Add(row.Type), $"{row.Type} is listed twice");
            Assert.Contains(row.Category, newCategories);
            Assert.True(row.MinBars >= 1, $"{row.Type}: MinBars");
            Assert.True(typeof(CoreIndicatorParameterBase).IsAssignableFrom(row.ParameterClass), $"{row.Type}: ParameterClass");
            Assert.NotNull(row.ParameterClass.GetConstructor(Type.EmptyTypes));

            ICoreIndicator? indicator = factory.Create(row.Type);
            var flag = Assert.IsAssignableFrom<CorePatternFlagIndicatorBase>(indicator);
            Assert.Equal(row, flag.Spec);
            Assert.False(flag.IsOverlay);

            CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == row.Type);
            Assert.Equal(row.Category, settings.Category);
            Assert.False(settings.IsOverlay);
            Assert.Equal(row.HasDirection ? IndicatorDefaultConstants.PatternFlagBidirectionalMinValue : IndicatorDefaultConstants.PatternFlagMinValue, settings.MinValue);
            Assert.Equal(IndicatorDefaultConstants.PatternFlagMaxValue, settings.MaxValue);
            Assert.Equal(SeriesPrimitiveType.Histogram, SeriesRenderStyleCatalog.ResolveDefaultPrimitive(settings, IndicatorResult.MainSeriesName));
            Assert.True(SeriesRenderStyleCatalog.SupportsRenderStyleSelection(row.Type));

            Assert.Equal(row.HasDirection, IndicatorChartSuppression.IsSuppressed(row.Type, IndicatorSeriesNames.PatternDirection));
            Assert.Equal(row.HasDirection || row.HasRules, IndicatorChartSuppression.IsSuppressed(row.Type, IndicatorResult.MainSeriesName)); // Main is a hidden helper next to visible side/rule series
        }
    }

    [Fact]
    public void CandleData_HasNoManagedReferences_SafeForUnclearArrayPoolReturn()
    {
        Assert.False(System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<CandleData>());
    }
}
