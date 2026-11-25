using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Real bars for the Flag engine tests (plan ChartPatterns56 v2, run V2-4). A quiet start (30 bars, true range 1), a pole of rising bars, a flag of bars whose highs lie on a slightly falling line with the lows
/// a constant width below, and a break bar; the numbers are chosen so that every rule of the default settings holds with room, and each test moves one of them to its edge. The bearish flag is the mirror image
/// around the start price (<see cref="FormationFixtures.Mirror"/>).
/// </summary>
internal static class FlagFixtures
{
    public const int QuietBars = 30;
    public const decimal Start = 100m;

    /// <summary>The level of the quiet bars: above the pole start, so a longer pole that reaches back into the quiet bars has a smaller move than the real one.</summary>
    public const decimal QuietLevel = 101m;
    public const long PoleVolume = 3000;
    public const long FlagVolume = 1000;
    public const long BreakVolume = 3000;
    public const long QuietVolume = 1000;
    public const int TailBars = 10;

    /// <summary>Settings that let random walks form flags: a small pole, loose channel rules, no volume rule (the walks have a constant volume).</summary>
    public static StockAnalyzer.Core.Models.Parameters.CoreFlagPatternParameter RelaxedFlag() => new()
    {
        MinPoleMovePercent = 2m,
        MinPoleAtrMultiplier = 1m,
        MinPoleBodyRatio = 0.4m,
        MinRSquared = 0.3m,
        MaxSlopeDiffAtr = 1m,
        RequireVolumeConfirmation = false,
        MaxChannelWidthRatio = 0.8m,
    };

    public sealed record FlagSpec
    {
        public bool Bullish { get; init; } = true;
        public int PoleBars { get; init; } = 5;

        /// <summary>The net rise of the pole (close of its last bar minus the start price).</summary>
        public decimal PoleRise { get; init; } = 12m;

        /// <summary>Offsets (0-based) inside the pole of bars that close down; the other bars rise by the amount that keeps the net rise.</summary>
        public IReadOnlySet<int> DownBars { get; init; } = new HashSet<int>();

        public int FlagBars { get; init; } = 10;

        /// <summary>The fall of both rails per bar.</summary>
        public decimal Drift { get; init; } = 0.2m;

        /// <summary>The extra fall per bar of the lower rail (0 = parallel).</summary>
        public decimal LowerExtraDrift { get; init; }

        public decimal Width { get; init; } = 3m;

        /// <summary>Flag bar offsets (1-based, bar 0 is the pole end) whose high is lifted by <see cref="SpikeSize"/> (an outlier of the upper rail).</summary>
        public int? SpikeAt { get; init; }

        public decimal SpikeSize { get; init; }

        public long PoleBarVolume { get; init; } = PoleVolume;
        public long FlagBarVolume { get; init; } = FlagVolume;
        public long BreakBarVolume { get; init; } = BreakVolume;

        /// <summary>Close of the break bar; null = a close 1.8 above the extrapolated upper rail.</summary>
        public decimal? BreakClose { get; init; }

        public decimal? BreakOpen { get; init; }

        /// <summary>A bar of the quiet start with a high at this price (inside the 20-bar zone before the pole end), or null.</summary>
        public decimal? PreSpikeHigh { get; init; }

        public bool RisingTail { get; init; }
    }

    public sealed record Built(CandleData[] Bars, int PoleStart, int PoleEnd, int BreakBar);

    public static Built Build(FlagSpec s)
    {
        var bars = new List<CandleData>();
        for (int i = 0; i < QuietBars; i++)
        {
            decimal high = s.PreSpikeHigh.HasValue && i == QuietBars - 5 ? s.PreSpikeHigh.Value : QuietLevel + 0.5m;
            bars.Add(PatternEngineTestBars.Bar(i, QuietLevel, high, QuietLevel - 0.5m, QuietLevel, QuietVolume));
        }

        int poleStart = bars.Count;
        int downs = s.DownBars.Count;
        int ups = s.PoleBars - downs;
        const decimal downStep = 0.1m;
        decimal upStep = (s.PoleRise + downs * downStep) / ups;
        decimal price = Start;
        for (int j = 0; j < s.PoleBars; j++)
        {
            decimal open = price;
            decimal close = s.DownBars.Contains(j) ? open - downStep : open + upStep;
            bars.Add(PatternEngineTestBars.Bar(bars.Count, open, Math.Max(open, close) + 0.2m, Math.Min(open, close) - 0.2m, close, s.PoleBarVolume));
            price = close;
        }

        int poleEnd = bars.Count - 1;
        decimal poleHigh = bars[poleEnd].High;
        for (int k = 1; k < s.FlagBars; k++)
        {
            decimal high = poleHigh - s.Drift * k + (s.SpikeAt == k ? s.SpikeSize : 0m);
            decimal low = poleHigh - s.Width - (s.Drift + s.LowerExtraDrift) * k;
            decimal open = high - 1.0m;
            decimal close = high - 1.2m;
            bars.Add(PatternEngineTestBars.Bar(bars.Count, open, high, low, close, s.FlagBarVolume));
        }

        int breakBar = poleEnd + s.FlagBars;
        decimal rail = poleHigh - s.Drift * s.FlagBars;
        decimal breakClose = s.BreakClose ?? rail + 1.8m;
        decimal breakOpen = s.BreakOpen ?? breakClose - 2.0m;
        bars.Add(PatternEngineTestBars.Bar(breakBar, breakOpen, Math.Max(breakOpen, breakClose) + 0.3m, Math.Min(breakOpen, breakClose) - 0.2m, breakClose, s.BreakBarVolume));

        decimal tailPrice = breakClose;
        for (int i = 0; i < TailBars; i++)
        {
            decimal next = s.RisingTail ? tailPrice + 0.3m : tailPrice;
            bars.Add(PatternEngineTestBars.Bar(bars.Count, tailPrice, Math.Max(tailPrice, next) + 0.2m, Math.Min(tailPrice, next) - 0.2m, next, FlagVolume));
            tailPrice = next;
        }

        CandleData[] result = bars.ToArray();
        return new Built(s.Bullish ? result : FormationFixtures.Mirror(result), poleStart, poleEnd, breakBar);
    }
}
