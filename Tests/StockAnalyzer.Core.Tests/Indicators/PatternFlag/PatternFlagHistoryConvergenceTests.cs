using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.PatternFlags;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using StockAnalyzer.Core.Tests.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// Plan ChartPatterns56, S-3 model P and decision U-10: the whole-series flags (BOS, CHoCH) depend on where the series starts until the first confirmed breaks, so the claim is "the
/// recent bars do not": the last <see cref="CheckedBars"/> bars of the output computed on the last <see cref="LoadedBars"/> bars equal the same bars of the output computed on the whole series.
/// The three numbers are test fixtures, not product constants. A failure is reported to the owner (a named convergence constant would be an owner decision), not patched.
/// The formations run with relaxed settings (the default rules find almost nothing on a random walk, which would make the comparison vacuous).
/// </summary>
public class PatternFlagHistoryConvergenceTests
{
    private const int WholeSeriesBars = 2000;
    private const int LoadedBars = 500;
    private const int CheckedBars = 100;
    private const decimal StartPrice = 100m;
    private static readonly int[] Seeds = { 1, 2, 3, 4, 5, 6 };

    public static IEnumerable<object[]> ModelPTypes() => PatternFlagIndicatorCatalog.Rows
        .Where(r => r.Model == PatternFlagEvaluationModel.FullSeries)
        .Select(r => new object[] { r.Type });

    [Fact]
    public void TheCatalogHasModelPTypes()
    {
        Assert.NotEmpty(ModelPTypes());
    }

    [Theory]
    [MemberData(nameof(ModelPTypes))]
    public void TheRecentBars_DoNotDependOnHowMuchHistoryWasLoaded(IndicatorType type)
    {
        var factory = new IndicatorFactory();
        int ones = 0;

        foreach (int seed in Seeds)
        {
            CoreCandleData[] whole = SyntheticBars.SeededRandomWalk(seed, WholeSeriesBars, StartPrice)
                .Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();
            CoreCandleData[] tail = whole.Skip(WholeSeriesBars - LoadedBars).ToArray();

            IIndicatorResult fullResult = factory.Create(type, FormationFixtures.RelaxedParameters(type))!.Calculate(whole);
            IIndicatorResult tailResult = factory.Create(type, FormationFixtures.RelaxedParameters(type))!.Calculate(tail);

            foreach (string series in new[] { IndicatorResult.MainSeriesName, IndicatorSeriesNames.PatternDirection }.Where(fullResult.HasSeries))
            {
                var expected = fullResult.GetSeries(series).Skip(WholeSeriesBars - CheckedBars).ToArray();
                var actual = tailResult.GetSeries(series).Skip(LoadedBars - CheckedBars).ToArray();

                Assert.True(expected.SequenceEqual(actual), $"{type} {series} seed {seed}: the last {CheckedBars} bars differ between {WholeSeriesBars} and {LoadedBars} loaded bars");
                ones += expected.Count(v => v == 1m);
            }
        }

        if (ones == 0 && CandlestickPatternEngine.Supports(type))
        {
            CandleData[] suffix = CandleFixtureBuilder.Part1Tail(type);
            CandleData[] prefix = SyntheticBars.SeededRandomWalk(1, WholeSeriesBars - suffix.Length, StartPrice).ToArray();
            CoreCandleData[] combined = prefix.Concat(suffix)
                .Select((b, idx) => new CoreCandleData(SyntheticBars.Start.AddDays(idx), b.Open, b.High, b.Low, b.Close, b.Volume))
                .ToArray();
            CoreCandleData[] tail = combined.Skip(combined.Length - LoadedBars).ToArray();

            IIndicatorResult fullResult = factory.Create(type, FormationFixtures.RelaxedParameters(type))!.Calculate(combined);
            IIndicatorResult tailResult = factory.Create(type, FormationFixtures.RelaxedParameters(type))!.Calculate(tail);
            var expected = fullResult.GetSeries(IndicatorResult.MainSeriesName).Skip(combined.Length - CheckedBars).ToArray();
            var actual = tailResult.GetSeries(IndicatorResult.MainSeriesName).Skip(LoadedBars - CheckedBars).ToArray();
            Assert.True(expected.SequenceEqual(actual), $"{type} Main augmented tail: last {CheckedBars} bars differ between {combined.Length} and {LoadedBars} loaded bars");
            ones += expected.Count(v => v == 1m);
        }

        Assert.True(ones > 0, "the fixtures show no flag in the checked bars");
    }
}
