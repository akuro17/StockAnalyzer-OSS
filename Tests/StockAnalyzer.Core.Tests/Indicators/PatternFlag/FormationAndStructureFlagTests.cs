using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Models.Indicators.PatternFlags.Formations;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// The six formation and market-structure flags (plan ChartPatterns56, 5.2 and 5.3, run 3): registration, catalog, parameter classes and the Higher High / Higher Low flag, which still compares with an oracle that calls the
/// existing detector on the bars of its window (model W). The other five have their own engines and tests since runs V2-3 to V2-5.
/// </summary>
public class FormationAndStructureFlagTests
{
    private static readonly JsonSerializerOptions WorkspaceOptions = new() { TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver() };
    private static readonly IndicatorFactory Factory = new();

    private const int Window = ChartConstants.PatternDetectionWindowBars;
    private const int WalkBars = 1000;
    private const decimal WalkStart = 100m;
    private static readonly int[] Seeds = { 1, 2, 3, 4 };

    /// <summary>A freshness at least as long as the window: every pattern of the window counts, which reproduces the legacy "no freshness limit" behaviour.</summary>
    private const int NoFreshnessLimit = PatternFlagParameterLimits.MaxBarsSinceConfirmationMax;

    private static CandleData[] Walk(int seed, int count = WalkBars) => SyntheticBars.SeededRandomWalk(seed, count, WalkStart).ToArray();

    private static CoreCandleData[] ToCore(IEnumerable<CandleData> bars)
        => bars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();

    private static IIndicatorResult Run(IndicatorType type, IEnumerable<CandleData> bars, CoreIndicatorParameterBase? parameters = null)
    {
        IIndicatorResult result = Factory.Create(type, parameters)!.Calculate(ToCore(bars));
        Assert.True(result.IsSuccessful, result.ErrorMessage);
        return result;
    }

    private static IReadOnlyList<decimal?> Main(IndicatorType type, IEnumerable<CandleData> bars, CoreIndicatorParameterBase? parameters = null)
        => Run(type, bars, parameters).GetSeries(IndicatorResult.MainSeriesName);

    private static CandleData[] WindowAt(CandleData[] bars, int t)
    {
        int start = Math.Max(0, t - Window + 1);
        return bars.Skip(start).Take(t - start + 1).ToArray();
    }

    private static readonly IndicatorType[] AllSix =
    {
        IndicatorType.PatternHeadAndShoulders, IndicatorType.PatternDoubleTopBottom, IndicatorType.PatternFlag,
        IndicatorType.MarketStructureBos, IndicatorType.MarketStructureChoch, IndicatorType.MarketStructureHhHl,
        IndicatorType.MarketStructureLlLh,
    };

    public static IEnumerable<object[]> SixTypes() => AllSix.Select(t => new object[] { t });

    // ---------------------------------------------------------------- catalog and registration

    [Theory]
    [MemberData(nameof(SixTypes))]
    public void EachType_IsRegisteredWithItsCatalogRow(IndicatorType type)
    {
        Assert.True(PatternFlagIndicatorCatalog.TryGet(type, out PatternFlagSpec spec));
        var indicator = Assert.IsAssignableFrom<CorePatternFlagIndicatorBase>(Factory.Create(type));

        Assert.Equal(spec, indicator.Spec);
        Assert.False(indicator.IsOverlay);
        Assert.Equal(type is not (IndicatorType.MarketStructureHhHl or IndicatorType.MarketStructureLlLh), spec.HasDirection);
        Assert.Equal(PatternFlagEvaluationModel.FullSeries, spec.Model);
        Assert.IsType(spec.ParameterClass, indicator.GetDefaultSettings().ParameterObject);
        Assert.Equal(PatternFlagIndicatorCatalog.IsHelperSeries(type, IndicatorSeriesNames.PatternDirection), spec.HasDirection);
    }

    [Fact]
    public void TheCatalog_Holds_43Candles_3Formations_4Structures()
    {
        Assert.Equal(3, PatternFlagIndicatorCatalog.Rows.Count(r => r.Category == CoreIndicatorCategory.PatternRecognition));
        Assert.Equal(4, PatternFlagIndicatorCatalog.Rows.Count(r => r.Category == CoreIndicatorCategory.MarketStructure));
        Assert.Equal(43, PatternFlagIndicatorCatalog.Rows.Count(r => r.Category == CoreIndicatorCategory.CandlestickPatterns));
    }

    [Fact]
    public void TheDescriptions_ContinueTheScreenerCriteriaNames_AndAreDistinctFromTheLegacyStructureShift()
    {
        Assert.Equal("Head and Shoulders", IndicatorType.PatternHeadAndShoulders.GetDescription());
        Assert.Equal("Double Top / Bottom", IndicatorType.PatternDoubleTopBottom.GetDescription());
        Assert.Equal("Bullish / Bearish Flag", IndicatorType.PatternFlag.GetDescription());
        Assert.Equal("Break of Structure (BOS)", IndicatorType.MarketStructureBos.GetDescription());
        Assert.Equal("Change of Character (CHoCH)", IndicatorType.MarketStructureChoch.GetDescription());
        Assert.Equal("Higher High / Higher Low", IndicatorType.MarketStructureHhHl.GetDescription());
        Assert.Equal("Lower Low / Lower High", IndicatorType.MarketStructureLlLh.GetDescription());
        Assert.NotEqual(IndicatorType.MarketStructureShift.GetDescription(), IndicatorType.MarketStructureBos.GetDescription());
    }

    // ---------------------------------------------------------------- Head and Shoulders / Double Top and Bottom
    // The two formations are evaluated by their own engines since run V2-3 (tests: PatternEngines/HeadAndShouldersEngineTests, DoubleTopBottomEngineTests, FormationIndicatorTests);
    // the legacy-predicate parity tests of runs 1-3 were retired with the adapter.

    // ---------------------------------------------------------------- Flag
    // The Flag is evaluated by its own engine since run V2-4 (tests: PatternEngines/FlagEngineTests, Indicators/PatternFlag/FlagIndicatorTests); the tests of the detector adapter were retired with it.

    // ---------------------------------------------------------------- Higher High / Higher Low
    // Evaluated by HigherHighHigherLowEngine since run V2-10 (tests: PatternEngines/HigherHighHigherLowEngineTests, Indicators/PatternFlag/CandlestickPart3AndHhHlIndicatorTests);
    // the Run 3 detector parity tests and Model W tests were retired with CandleWindowView and IsHigherHighHigherLowConfirmed.

    // ---------------------------------------------------------------- BOS / CHoCH
    // BOS and CHoCH are evaluated by their own engine since run V2-5 (tests: PatternEngines/StructureShiftEngineTests, Indicators/PatternFlag/StructureShiftIndicatorTests); the oracle tests against the
    // legacy DetectCausal and the P-9 comparison with the non-causal Detect were retired with the adapter (the legacy detector itself is unchanged and keeps its own tests).

    // ---------------------------------------------------------------- parameter classes

    [Fact]
    public void TheFourParameterClasses_RoundTripThroughTheWorkspaceJson_WithTheirTypeNameAndKeepDefaultsForMissingProperties()
    {
        CoreIndicatorParameterBase[] originals =
        {
            new CoreHeadAndShouldersParameter { ZigZagThresholdPercent = 4.5m, SignalPersistenceBars = 9, DirectionMode = PatternDirectionMode.BearishOnly, MaxTotalBars = 150, ShoulderTolerancePercent = 4.0m },
            new CoreDoubleTopBottomParameter { ZigZagThresholdPercent = 5.5m, SignalPersistenceBars = 8, DirectionMode = PatternDirectionMode.BullishOnly, MaxPeakIntervalBars = 55, Peak2ZoneThreshold = 0.7m },
            new CoreFlagPatternParameter { MinPoleBars = 4, MaxFlagBars = 20, MinRSquared = 0.8m, DirectionMode = PatternDirectionMode.BullishOnly },
            new CoreStructureShiftParameter { ZigZagThresholdPercent = 6.5m, SignalPersistenceBars = 11, DirectionMode = PatternDirectionMode.BearishOnly },
            new CoreStructureTrendParameter { ZigZagThresholdPercent = 2.5m },
        };

        foreach (CoreIndicatorParameterBase original in originals)
        {
            string json = JsonSerializer.Serialize(original, WorkspaceOptions);
            Assert.Contains($"\"$type\":\"{original.GetType().Name}\"", json);

            var back = JsonSerializer.Deserialize<CoreIndicatorParameterBase>(json, WorkspaceOptions)!;
            Assert.IsType(original.GetType(), back);
            Assert.Equal(JsonSerializer.Serialize(original, WorkspaceOptions), JsonSerializer.Serialize(back, WorkspaceOptions));

            var defaults = (CoreIndicatorParameterBase)JsonSerializer.Deserialize($"{{\"$type\":\"{original.GetType().Name}\"}}", typeof(CoreIndicatorParameterBase), WorkspaceOptions)!;
            Assert.Equal(JsonSerializer.Serialize(Activator.CreateInstance(original.GetType()), WorkspaceOptions), JsonSerializer.Serialize(defaults, WorkspaceOptions));
        }
    }

    [Fact]
    public void TheDefaults_AreTheDocumentedOnes()
    {
        CoreFormationPatternParameterBase[] formations = { new CoreHeadAndShouldersParameter(), new CoreDoubleTopBottomParameter() };
        var flag = new CoreFlagPatternParameter();
        var shift = new CoreStructureShiftParameter();
        var trend = new CoreStructureTrendParameter();

        Assert.All(formations, formation => Assert.Equal((3.0m, 5, PatternDirectionMode.Both), (formation.ZigZagThresholdPercent, formation.SignalPersistenceBars, formation.DirectionMode)));
        Assert.Equal((3, 15, 5, 25, 5, PatternDirectionMode.Both), (flag.MinPoleBars, flag.MaxPoleBars, flag.MinFlagBars, flag.MaxFlagBars, flag.SignalPersistenceBars, flag.DirectionMode));
        Assert.Equal((5.0m, 0, PatternDirectionMode.Both, 1.20m), (shift.ZigZagThresholdPercent, shift.SignalPersistenceBars, shift.DirectionMode, shift.BreakoutVolumeMultiplier));
        Assert.Equal(1.15m, Assert.IsType<CoreStructureShiftParameter>(Factory.Create(IndicatorType.MarketStructureBos)!.GetDefaultSettings().ParameterObject).BreakoutVolumeMultiplier);
        Assert.Equal(1.25m, Assert.IsType<CoreStructureShiftParameter>(Factory.Create(IndicatorType.MarketStructureChoch)!.GetDefaultSettings().ParameterObject).BreakoutVolumeMultiplier);
        Assert.Equal(3.0m, trend.ZigZagThresholdPercent);
    }

    [Fact]
    public void Validate_RejectsEveryValueOutsideItsRange_AndAcceptsTheBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHeadAndShouldersParameter { ZigZagThresholdPercent = 0.4m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHeadAndShouldersParameter { ZigZagThresholdPercent = 20.1m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHeadAndShouldersParameter { SignalPersistenceBars = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHeadAndShouldersParameter { SignalPersistenceBars = 51 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreHeadAndShouldersParameter { DirectionMode = (PatternDirectionMode)9 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreFlagPatternParameter { MaxFlagBars = 31 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStructureShiftParameter { ZigZagThresholdPercent = 30.1m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStructureShiftParameter { SignalPersistenceBars = 51 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreStructureTrendParameter { ZigZagThresholdPercent = 0m }.Validate());

        new CoreHeadAndShouldersParameter { ZigZagThresholdPercent = 0.5m, SignalPersistenceBars = 0 }.Validate();
        new CoreDoubleTopBottomParameter { ZigZagThresholdPercent = 20.0m, SignalPersistenceBars = 50 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreDoubleTopBottomParameter { ZigZagThresholdPercent = 20.1m }.Validate());
        new CoreFlagPatternParameter { MaxFlagBars = 30 }.Validate();
        new CoreStructureShiftParameter { ZigZagThresholdPercent = 30.0m, SignalPersistenceBars = 0 }.Validate();
        new CoreStructureTrendParameter { ZigZagThresholdPercent = 30.0m }.Validate();
    }

    [Fact]
    public void ThePatternDirectionModeValues_ArePersistedAndNeverRenumbered()
    {
        Assert.Equal(0, (int)PatternDirectionMode.Both);
        Assert.Equal(1, (int)PatternDirectionMode.BullishOnly);
        Assert.Equal(2, (int)PatternDirectionMode.BearishOnly);
    }
}
