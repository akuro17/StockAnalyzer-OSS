using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ElliottWave;
using StockAnalyzer.Core.Models.HarmonicPattern;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Analysis;

public class PatternOverlapDeterminismTests
{
    [Fact]
    public void HarmonicPattern_FilterOverlappingPatterns_PermutationInvariant()
    {
        DateTime baseDate = new(2025, 1, 1);
        var baseList = new List<HarmonicPatternResult>();

        // Build 32 patterns with identical tier (>=0.80), span (20), and confidence score (0.85)
        // Deliberately vary D.Index, X.Index, PatternType, IsBullish to exercise tie-breakers
        var patternTypes = new[]
        {
            HarmonicPatternType.Gartley,
            HarmonicPatternType.Bat,
            HarmonicPatternType.Butterfly,
            HarmonicPatternType.Crab
        };

        for (int i = 0; i < 32; i++)
        {
            int xIdx = i * 2;
            int dIdx = xIdx + 20;
            var pt = patternTypes[i % patternTypes.Length];
            bool isBull = (i % 2 == 0);

            var x = new PivotPoint(xIdx, baseDate.AddDays(xIdx), 100m, !isBull);
            var a = new PivotPoint(xIdx + 5, baseDate.AddDays(xIdx + 5), 120m, isBull);
            var b = new PivotPoint(xIdx + 10, baseDate.AddDays(xIdx + 10), 105m, !isBull);
            var c = new PivotPoint(xIdx + 15, baseDate.AddDays(xIdx + 15), 115m, isBull);
            var d = new PivotPoint(dIdx, baseDate.AddDays(dIdx), 95m, !isBull);

            baseList.Add(new HarmonicPatternResult(pt, x, a, b, c, d, 0.85, 94m, 96m, isBull));
        }

        var baselineFiltered = HarmonicPatternDetector.FilterOverlappingPatterns(new List<HarmonicPatternResult>(baseList))
            .Select(p => (p.PatternType, p.X.Index, p.D.Index, p.IsBullish))
            .ToList();

        var rng = new Random(42);
        for (int run = 0; run < 50; run++)
        {
            var shuffled = baseList.OrderBy(_ => rng.Next()).ToList();
            var filtered = HarmonicPatternDetector.FilterOverlappingPatterns(shuffled)
                .Select(p => (p.PatternType, p.X.Index, p.D.Index, p.IsBullish))
                .ToList();

            Assert.Equal(baselineFiltered, filtered);
        }
    }

    [Fact]
    public void ElliottWave_FilterOverlappingPatterns_PermutationInvariant()
    {
        DateTime baseDate = new(2025, 1, 1);
        var baseList = new List<ElliottWaveResult>();

        // Build 32 patterns with identical tier (>=0.70), span (25), and confidence score (0.75)
        for (int i = 0; i < 32; i++)
        {
            int startIdx = i * 3;
            int endIdx = startIdx + 25;
            bool isImpulse = (i % 2 == 0);
            bool isBullish = (i % 4 < 2);

            var pivots = new List<PivotPoint>
            {
                new(startIdx, baseDate.AddDays(startIdx), 100m, !isBullish),
                new(startIdx + 8, baseDate.AddDays(startIdx + 8), 120m, isBullish),
                new(startIdx + 16, baseDate.AddDays(startIdx + 16), 110m, !isBullish),
                new(endIdx, baseDate.AddDays(endIdx), 130m, isBullish)
            };

            baseList.Add(new ElliottWaveResult(
                isImpulse,
                isBullish,
                pivots,
                0.75,
                isImpulse ? ElliottWavePhase.Wave5 : ElliottWavePhase.WaveC));
        }

        var baselineFiltered = ElliottWaveDetector.FilterOverlappingPatterns(new List<ElliottWaveResult>(baseList))
            .Select(p => (p.IsImpulse, p.IsBullish, p.StartIndex, p.EndIndex))
            .ToList();

        var rng = new Random(42);
        for (int run = 0; run < 50; run++)
        {
            var shuffled = baseList.OrderBy(_ => rng.Next()).ToList();
            var filtered = ElliottWaveDetector.FilterOverlappingPatterns(shuffled)
                .Select(p => (p.IsImpulse, p.IsBullish, p.StartIndex, p.EndIndex))
                .ToList();

            Assert.Equal(baselineFiltered, filtered);
        }
    }

    [Fact]
    public void CriteriaEvaluation_ParallelAndSequentialProduceIdenticalResults()
    {
        // 200 seeded random walk series (300 bars)
        const int symbolCount = 200;
        const int barCount = 300;
        var symbolsData = new List<IReadOnlyList<CandleData>>(symbolCount);
        for (int s = 1; s <= symbolCount; s++)
        {
            symbolsData.Add(SyntheticBars.SeededRandomWalk(s, barCount, 100m));
        }

        string[] criteriaNames =
        {
            "Granville's Law Buy Signal",
            "Gartley",
            "Elliott Wave Impulse 1-5",
            "Bullish Engulfing",
            "Double Top",
            "Break of Structure"
        };

        var configs = criteriaNames.Select(name => new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            CustomDisplayName = name,
            OutputName = name
        }).ToList();

        var extractor = ScreenerValueExtractor.Default;

        // Baseline: sequential evaluation
        var baselineResults = new decimal?[symbolCount, criteriaNames.Length];
        for (int s = 0; s < symbolCount; s++)
        {
            var candles = symbolsData[s];
            for (int c = 0; c < configs.Count; c++)
            {
                baselineResults[s, c] = extractor.ExtractValueNullable(configs[c], candles);
            }
        }

        // Parallel evaluation repeated 5 times with degree 8
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 8 };
        for (int run = 0; run < 5; run++)
        {
            var parallelResults = new decimal?[symbolCount, criteriaNames.Length];
            Parallel.For(0, symbolCount, parallelOptions, s =>
            {
                var candles = symbolsData[s];
                for (int c = 0; c < configs.Count; c++)
                {
                    parallelResults[s, c] = extractor.ExtractValueNullable(configs[c], candles);
                }
            });

            for (int s = 0; s < symbolCount; s++)
            {
                for (int c = 0; c < configs.Count; c++)
                {
                    Assert.Equal(baselineResults[s, c], parallelResults[s, c]);
                }
            }
        }
    }
}
