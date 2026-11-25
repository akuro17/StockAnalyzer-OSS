using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Real bars for the Head and Shoulders and Double Top / Bottom engine tests (plan ChartPatterns56 v2, run V2-3). A series is built from anchors (bar index, close): the closes are linear between the anchors, every
/// bar opens at the previous close and has a wick on both sides, so the numbers of the pivots (close of the anchor bar plus the wick) are known exactly and each test can sit on a rule's edge.
/// </summary>
internal static class FormationFixtures
{
    /// <summary>The wick on both sides of every bar.</summary>
    public const decimal Wick = 0.3m;

    /// <summary>The volume of every bar unless a test sets one.</summary>
    public const long BaseVolume = 1000;

    /// <summary>A volume well above 1.25 times the base average.</summary>
    public const long BreakVolume = 5000;

    /// <summary>Bars after the break bar in the fixtures, enough for every persistence the tests use.</summary>
    public const int TailBars = 12;

    /// <summary>Start price of the run-up.</summary>
    public const decimal Start = 100m;

    public static CandleData[] FromAnchors(IReadOnlyList<(int Index, decimal Price)> anchors, IReadOnlyDictionary<int, long>? volumes = null, decimal wick = Wick)
    {
        int count = anchors[^1].Index + 1;
        var bars = new CandleData[count];
        decimal previousClose = anchors[0].Price;
        int segment = 0;

        for (int i = 0; i < count; i++)
        {
            while (segment < anchors.Count - 2 && i > anchors[segment + 1].Index)
            {
                segment++;
            }

            (int i0, decimal p0) = anchors[segment];
            (int i1, decimal p1) = anchors[Math.Min(segment + 1, anchors.Count - 1)];
            decimal close = i1 == i0 ? p1 : p0 + (p1 - p0) * (i - i0) / (i1 - i0);
            decimal open = i == 0 ? close : previousClose;
            long volume = volumes != null && volumes.TryGetValue(i, out long v) ? v : BaseVolume;
            bars[i] = PatternEngineTestBars.Bar(i, open, Math.Max(open, close) + wick, Math.Min(open, close) - wick, close, volume);
            previousClose = close;
        }

        return bars;
    }

    /// <summary>Settings that let random walks form Head and Shoulders: no prior trend, a low zone threshold, no volume rule (the walks have a constant volume) and loose geometry.</summary>
    public static CoreHeadAndShouldersParameter RelaxedHeadAndShoulders() => new()
    {
        RequirePriorTrend = false,
        PriceZoneThreshold = 0.5m,
        RequireVolumeConfirmation = false,
        MinHeadProminenceRatio = 0.01m,
        ShoulderTolerancePercent = 20m,
        MaxNecklineSlopeRatio = 1m,
        ZigZagThresholdPercent = 2m,
    };

    /// <summary>Settings that let random walks form Double Tops and Bottoms: no prior trend, the lowest zone thresholds and no volume rule.</summary>
    public static CoreDoubleTopBottomParameter RelaxedDouble() => new()
    {
        RequirePriorTrend = false,
        Peak1ZoneThreshold = 0.5m,
        Peak2ZoneThreshold = 0.5m,
        RequireVolumeConfirmation = false,
    };

    /// <summary>Settings that let random walks form Harmonic patterns: wide ratio bands, low leg thresholds, low score floor and cleanliness off.</summary>
    public static CoreHarmonicPatternParameter RelaxedHarmonic() => new()
    {
        MinLegSwingPercent = 0.5m,
        MinLegAtrMultiplier = 0.0m,
        MinSpanBars = 6,
        MaxSpanBars = 200,
        MinLegBars = 1,
        MaxLegBars = 80,
        MaxTimeSymmetryRatio = 20.0m,
        AbXaMin = 0.10m,
        AbXaMax = 1.50m,
        BcAbMin = 0.10m,
        BcAbMax = 1.50m,
        CdBcMin = 0.50m,
        CdBcMax = 4.00m,
        AdXaMin = 0.30m,
        AdXaMax = 3.00m,
        MinConfidenceScore = 0.10m,
        RequireIntermediateCleanliness = false,
    };

    /// <summary>Settings that let random walks form Elliott Impulse patterns: small ZigZag step, no ATR filter, origin zone off and permissive tolerances.</summary>
    public static CoreElliottImpulseParameter RelaxedElliottImpulse() => new()
    {
        ZigZagThresholdPercent = 1.5m,
        MinSwingAtrMultiplier = 0.0m,
        RequireOriginPriceZone = false,
        MinSpanBars = 10,
        MaxSpanBars = 300,
        MaxTimeSymmetryRatio = 20.0m,
        OverlapTolerance = 0.25m,
        AllowTruncation = true,
        FibonacciTolerance = 0.40m,
        MinConfidenceScore = 0.10m,
    };

    /// <summary>Settings that let random walks form Elliott Corrective patterns: small ZigZag step, no ATR filter, origin zone off and permissive tolerances.</summary>
    public static CoreElliottCorrectiveParameter RelaxedElliottCorrective() => new()
    {
        ZigZagThresholdPercent = 1.5m,
        MinSwingAtrMultiplier = 0.0m,
        RequireOriginPriceZone = false,
        MinSpanBars = 6,
        MaxSpanBars = 300,
        MaxTimeSymmetryRatio = 20.0m,
        RequireWaveCExceedWaveA = false,
        FibonacciTolerance = 0.40m,
        MinConfidenceScore = 0.10m,
    };

    /// <summary>Settings that let random walks form Candlestick patterns on history-convergence checks while still exercising prior-trend EMA.</summary>
    public static CoreCandlePatternParameter RelaxedCandle(IndicatorType type)
    {
        CoreCandlePatternParameter p = CoreCandlePatternParameter.CreateFor(type);
        p.RequirePriorTrend = false;
        p.RequirePriceZone = false;
        p.RequireVolumeConfirmation = false;
        p.LargeBodyAvgRatio = 0.50m;
        p.LargeBodyAtrRatio = 0.10m;
        p.LargeRangeRatio = 0.20m;
        p.SmallBodyAvgRatio = 2.00m;
        p.SmallRangeRatio = 0.90m;
        p.DojiBodyRangeRatio = 0.20m;
        p.WickToleranceRangeRatio = 0.20m;
        if (p is CoreUmbrellaPatternParameter umbrella)
        {
            umbrella.LongShadowBodyMultiplier = 1.0m;
            umbrella.OppositeShadowBodyMaxRatio = 1.0m;
        }
        else if (p is CoreGapCandlePatternParameter gap)
        {
            gap.GapMinBodyRatio = 0.0m;
            gap.RequireWickToWickGap = false;
        }

        return p;
    }

    /// <summary>The relaxed settings of a formation, flag, harmonic, wave, or candle type, or null for every other type (its defaults already find patterns on a walk).</summary>
    public static CoreIndicatorParameterBase? RelaxedParameters(IndicatorType type) => type switch
    {
        IndicatorType.PatternHeadAndShoulders => RelaxedHeadAndShoulders(),
        IndicatorType.PatternDoubleTopBottom => RelaxedDouble(),
        IndicatorType.PatternFlag => FlagFixtures.RelaxedFlag(),
        IndicatorType.HarmonicGartley or IndicatorType.HarmonicButterfly or IndicatorType.HarmonicBat => RelaxedHarmonic(),
        IndicatorType.ElliottWaveImpulse => RelaxedElliottImpulse(),
        IndicatorType.ElliottWaveCorrective => RelaxedElliottCorrective(),
        _ when CandlestickPatternEngine.Supports(type) => RelaxedCandle(type),
        _ => null,
    };

    /// <summary>The pivot price of a rising or falling anchor bar: the close plus (minus) the wick.</summary>
    public static decimal PeakPrice(decimal close) => close + Wick;

    public static decimal ValleyPrice(decimal close) => close - Wick;

    /// <summary>The numbers of a Double Top fixture; the defaults give a pattern that satisfies every rule of the default settings.</summary>
    public sealed record DoubleSpec
    {
        public bool Top { get; init; } = true;
        public int FirstPeak { get; init; } = 60;
        public int Gap { get; init; } = 40;
        public decimal Peak1 { get; init; } = 150m;
        public decimal Peak2 { get; init; } = 150m;
        public decimal Valley { get; init; } = 138m;
        public int ValleyOffset { get; init; } = 20;
        public int BreakDelay { get; init; } = 9;
        public decimal BreakClose { get; init; } = 132m;
        public long BreakBarVolume { get; init; } = BreakVolume;
        public bool Spike { get; init; }
        public int Tail { get; init; } = TailBars;

        /// <summary>Extra anchors (bar, close) in the orientation of a top; they replace an anchor of the same bar and are sorted in.</summary>
        public IReadOnlyList<(int Index, decimal Price)>? ExtraAnchors { get; init; }
    }

    /// <summary>Result of a fixture: the bars and the bar on which the pattern is expected to break.</summary>
    public sealed record Built(CandleData[] Bars, int BreakBar);

    /// <summary>
    /// A double top: a run-up to the first peak, a valley, the second peak <c>Gap</c> bars after the first, a pull-back to just above the valley, then a close below it on the break bar. The
    /// bottom is the mirror image around the start price (every price p becomes 2 * Start - p), so a test of one side proves the other.
    /// </summary>
    public static Built Double(DoubleSpec s)
    {
        int secondPeak = s.FirstPeak + s.Gap;
        int breakBar = secondPeak + s.BreakDelay;
        decimal valleyClose = s.Valley;
        decimal pullback = s.Valley + 4m;

        var anchors = new List<(int, decimal)>();
        if (s.Spike)
        {
            // A higher high inside the 60-bar window of the first peak: the peaks are then in the middle of the range, not at its top (reported symptom a).
            anchors.Add((0, Start));
            anchors.Add((20, 190m));
            anchors.Add((40, 120m));
        }
        else
        {
            anchors.Add((0, Start));
        }

        anchors.Add((s.FirstPeak, s.Peak1 - Wick));
        anchors.Add((s.FirstPeak + s.ValleyOffset, valleyClose + Wick));
        anchors.Add((secondPeak, s.Peak2 - Wick));
        if (breakBar - 1 > secondPeak)
        {
            anchors.Add((breakBar - 1, pullback));
        }

        anchors.Add((breakBar, s.BreakClose));
        anchors.Add((breakBar + s.Tail, s.BreakClose));

        var volumes = new Dictionary<int, long> { [breakBar] = s.BreakBarVolume };
        CandleData[] bars = FromAnchors(Merge(anchors, s.ExtraAnchors), volumes);
        return new Built(s.Top ? bars : Mirror(bars), breakBar);
    }

    /// <summary>Puts the extra anchors into the list (an extra anchor replaces an anchor of the same bar) and sorts by bar.</summary>
    private static List<(int Index, decimal Price)> Merge(IEnumerable<(int Index, decimal Price)> anchors, IReadOnlyList<(int Index, decimal Price)>? extra)
    {
        var byBar = anchors.ToDictionary(a => a.Index, a => a.Price);
        foreach ((int index, decimal price) in extra ?? Array.Empty<(int, decimal)>())
        {
            byBar[index] = price;
        }

        return byBar.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>The mirror image of a series around <see cref="Start"/>: every price p becomes <c>2 * Start - p</c>, high and low swap (volumes are kept).</summary>
    public static CandleData[] Mirror(IReadOnlyList<CandleData> bars)
        => bars.Select((b, i) => PatternEngineTestBars.Bar(i, 2 * Start - b.Open, 2 * Start - b.Low, 2 * Start - b.High, 2 * Start - b.Close, b.Volume)).ToArray();

    /// <summary>The numbers of a Head and Shoulders fixture; the defaults satisfy every rule of the default settings.</summary>
    public sealed record HeadSpec
    {
        public bool Top { get; init; } = true;
        public int LeftShoulder { get; init; } = 50;
        public int Head { get; init; } = 80;
        public int RightShoulder { get; init; } = 110;
        public decimal LeftShoulderPrice { get; init; } = 150m;
        public decimal HeadPrice { get; init; } = 165m;
        public decimal RightShoulderPrice { get; init; } = 150m;
        public decimal Valley1 { get; init; } = 138m;
        public decimal Valley2 { get; init; } = 138m;
        public int BreakDelay { get; init; } = 5;
        public decimal BreakClose { get; init; } = 132m;
        public long BreakBarVolume { get; init; } = BreakVolume;
        public int Tail { get; init; } = TailBars;

        /// <summary>Extra anchors (bar, close) in the orientation of a top; they replace an anchor of the same bar and are sorted in.</summary>
        public IReadOnlyList<(int Index, decimal Price)>? ExtraAnchors { get; init; }
    }

    /// <summary>A head and shoulders top; the inverse is the mirror image.</summary>
    public static Built HeadAndShoulders(HeadSpec s)
    {
        int valley1Bar = (s.LeftShoulder + s.Head) / 2;
        int valley2Bar = (s.Head + s.RightShoulder) / 2;
        int breakBar = s.RightShoulder + s.BreakDelay;

        var anchors = new List<(int, decimal)>
        {
            (0, Start),
            (s.LeftShoulder, s.LeftShoulderPrice - Wick),
            (valley1Bar, s.Valley1 + Wick),
            (s.Head, s.HeadPrice - Wick),
            (valley2Bar, s.Valley2 + Wick),
            (s.RightShoulder, s.RightShoulderPrice - Wick),
            (breakBar, s.BreakClose),
            (breakBar + s.Tail, s.BreakClose),
        };

        if (breakBar - 1 > s.RightShoulder)
        {
            anchors.Add((breakBar - 1, Math.Min(s.Valley2, s.Valley1) + 4m));
        }

        var volumes = new Dictionary<int, long> { [breakBar] = s.BreakBarVolume };
        CandleData[] bars = FromAnchors(Merge(anchors, s.ExtraAnchors), volumes);
        return new Built(s.Top ? bars : Mirror(bars), breakBar);
    }
}
