using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Tests.Analysis;

public class MarketBreadthCalculatorTests
{
    private const double Precision = 1e-6;

    private static CandleData MakeCandle(DateTime date, decimal close, decimal volume = 1000m)
        => new(date, close, close, close, close, (long)volume);

    private static List<CandleData> MakeSeries(params (string Date, decimal Close, decimal Volume)[] items)
        => items.Select(x => MakeCandle(DateTime.Parse(x.Date, CultureInfo.InvariantCulture), x.Close, x.Volume)).ToList();

    [Fact]
    public void AllAdvancers_AccumulatesPositiveAdLineAndUpVolume()
    {
        // 2 tickers, both advancing on Day 2 and Day 3
        var tickerA = MakeSeries(
            ("2024-01-01", 100m, 1000m),
            ("2024-01-02", 105m, 1200m),
            ("2024-01-03", 110m, 1500m)
        );

        var tickerB = MakeSeries(
            ("2024-01-01", 50m, 500m),
            ("2024-01-02", 55m, 600m),
            ("2024-01-03", 60m, 700m)
        );

        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = tickerA,
            ["B"] = tickerB
        };

        var result = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters { AdRatioBars = 2 });

        Assert.Equal(3, result.DateCount);
        Assert.Equal(2, result.SymbolCount);

        // Day 1 (First day, no previous bars)
        Assert.Equal(0, result.Series.Advancers[0]);
        Assert.Equal(0, result.Series.Decliners[0]);
        Assert.Equal(0, result.Series.Unchanged[0]);
        Assert.Equal(0, result.Series.AdLine[0]);
        Assert.Null(result.Series.Trin[0]);
        Assert.Equal(0m, result.Series.Cvi[0]);

        // Day 2 (Both A and B rose)
        Assert.Equal(2, result.Series.Advancers[1]);
        Assert.Equal(0, result.Series.Decliners[1]);
        Assert.Equal(0, result.Series.Unchanged[1]);
        Assert.Equal(2, result.Series.AdLine[1]);
        Assert.Null(result.Series.AdRatio[1]); // 0 decliners in window
        Assert.Null(result.Series.Trin[1]); // 0 decliners
        Assert.Equal(1800m, result.Series.Cvi[1]); // 1200 + 600

        // Day 3 (Both rose again)
        Assert.Equal(2, result.Series.Advancers[2]);
        Assert.Equal(0, result.Series.Decliners[2]);
        Assert.Equal(4, result.Series.AdLine[2]); // 2 + 2 = 4
        Assert.Equal(4000m, result.Series.Cvi[2]); // 1800 + 2200 = 4000
    }

    [Fact]
    public void AllDecliners_AccumulatesNegativeAdLineAndDownVolume()
    {
        var tickerA = MakeSeries(
            ("2024-01-01", 100m, 1000m),
            ("2024-01-02", 90m, 2000m),
            ("2024-01-03", 80m, 3000m)
        );

        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = tickerA
        };

        var result = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters());

        Assert.Equal(3, result.DateCount);
        Assert.Equal(0, result.Series.Advancers[1]);
        Assert.Equal(1, result.Series.Decliners[1]);
        Assert.Equal(-1, result.Series.AdLine[1]);
        Assert.Equal(-2000m, result.Series.Cvi[1]);

        Assert.Equal(0, result.Series.Advancers[2]);
        Assert.Equal(1, result.Series.Decliners[2]);
        Assert.Equal(-2, result.Series.AdLine[2]);
        Assert.Equal(-5000m, result.Series.Cvi[2]);
    }

    [Fact]
    public void AllUnchanged_ZeroAdLine()
    {
        var tickerA = MakeSeries(
            ("2024-01-01", 100m, 1000m),
            ("2024-01-02", 100m, 1000m),
            ("2024-01-03", 100m, 1000m)
        );

        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = tickerA
        };

        var result = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters());

        Assert.Equal(0, result.Series.Advancers[1]);
        Assert.Equal(0, result.Series.Decliners[1]);
        Assert.Equal(1, result.Series.Unchanged[1]);
        Assert.Equal(0, result.Series.AdLine[1]);
        Assert.Equal(0m, result.Series.Cvi[1]);
    }

    [Fact]
    public void MissingDates_ComparesAgainstOwnPreviousBar_AndExcludesMissingDays()
    {
        // Ticker A trades on Days 1, 2, 3, 4
        var tickerA = MakeSeries(
            ("2024-01-01", 100m, 1000m),
            ("2024-01-02", 105m, 1000m),
            ("2024-01-03", 110m, 1000m),
            ("2024-01-04", 115m, 1000m)
        );

        // Ticker B is halted on Day 2, trades on Days 1, 3, 4
        // On Day 3: B was 50 on Day 1, now 60 on Day 3 -> Advance!
        var tickerB = MakeSeries(
            ("2024-01-01", 50m, 500m),
            ("2024-01-03", 60m, 500m),
            ("2024-01-04", 55m, 500m)
        );

        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = tickerA,
            ["B"] = tickerB
        };

        var result = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters());

        Assert.Equal(4, result.DateCount);
        Assert.Equal(new[] { "2024-01-01", "2024-01-02", "2024-01-03", "2024-01-04" }, result.Series.Dates);

        // Day 2: Only Ticker A traded. Advancers = 1, Decliners = 0.
        Assert.Equal(1, result.Series.Advancers[1]);
        Assert.Equal(0, result.Series.Decliners[1]);
        Assert.Equal(0, result.Series.Unchanged[1]);

        // Day 3: Both A (105->110) and B (50->60, own prev bar) advanced! Advancers = 2.
        Assert.Equal(2, result.Series.Advancers[2]);
        Assert.Equal(0, result.Series.Decliners[2]);

        // Day 4: A advanced (110->115), B declined (60->55).
        Assert.Equal(1, result.Series.Advancers[3]);
        Assert.Equal(1, result.Series.Decliners[3]);
    }

    [Fact]
    public void Trin_CalculatesArmsIndexCorrectly()
    {
        // Day 2:
        // Advancers = 2, UpVol = 2000
        // Decliners = 1, DownVol = 500
        // TRIN = (A / D) / (UpVol / DownVol) = (2 / 1) / (2000 / 500) = 2.0 / 4.0 = 0.5
        var tickerA = MakeSeries(("2024-01-01", 100m, 1000m), ("2024-01-02", 110m, 1000m));
        var tickerB = MakeSeries(("2024-01-01", 100m, 1000m), ("2024-01-02", 110m, 1000m));
        var tickerC = MakeSeries(("2024-01-01", 100m, 500m), ("2024-01-02", 90m, 500m));

        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = tickerA,
            ["B"] = tickerB,
            ["C"] = tickerC
        };

        var result = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters());

        Assert.Equal(2, result.DateCount);
        Assert.Null(result.Series.Trin[0]); // Day 1
        Assert.NotNull(result.Series.Trin[1]);
        Assert.Equal(0.5, result.Series.Trin[1]!.Value, Precision);
    }

    [Fact]
    public void PctAboveMa_CalculatesFractionOfTickersAboveTheirSma()
    {
        // 3 tickers, 3-bar SMA
        // On Day 3 (index 2):
        // Ticker A: closes [10, 20, 30], SMA = 20. Close 30 > 20 -> above!
        // Ticker B: closes [30, 20, 10], SMA = 20. Close 10 < 20 -> below!
        // Ticker C: closes [20, 20, 20], SMA = 20. Close 20 == 20 -> not strictly greater!
        var tickerA = MakeSeries(("2024-01-01", 10m, 100m), ("2024-01-02", 20m, 100m), ("2024-01-03", 30m, 100m));
        var tickerB = MakeSeries(("2024-01-01", 30m, 100m), ("2024-01-02", 20m, 100m), ("2024-01-03", 10m, 100m));
        var tickerC = MakeSeries(("2024-01-01", 20m, 100m), ("2024-01-02", 20m, 100m), ("2024-01-03", 20m, 100m));

        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = tickerA,
            ["B"] = tickerB,
            ["C"] = tickerC
        };

        var result = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters { MaBars = 3 });

        Assert.Null(result.Series.PctAboveMa[0]);
        Assert.Null(result.Series.PctAboveMa[1]);
        Assert.NotNull(result.Series.PctAboveMa[2]);
        // 1 ticker above out of 3 valid = 1/3
        Assert.Equal(1.0 / 3.0, result.Series.PctAboveMa[2]!.Value, Precision);
    }

    [Fact]
    public void NewHighsAndNewLows_EvaluatedCorrectly()
    {
        // Lookback = 3 bars
        // Day 3 (index 2):
        // Ticker A: closes [10, 20, 30] -> Close 30 is >= max(10, 20, 30) -> High!
        // Ticker B: closes [30, 20, 10] -> Close 10 is <= min(30, 20, 10) -> Low!
        // Ticker C: closes [10, 30, 20] -> Close 20 is not max (30) nor min (10) -> Neither!
        var tickerA = MakeSeries(("2024-01-01", 10m, 100m), ("2024-01-02", 20m, 100m), ("2024-01-03", 30m, 100m));
        var tickerB = MakeSeries(("2024-01-01", 30m, 100m), ("2024-01-02", 20m, 100m), ("2024-01-03", 10m, 100m));
        var tickerC = MakeSeries(("2024-01-01", 10m, 100m), ("2024-01-02", 30m, 100m), ("2024-01-03", 20m, 100m));

        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = tickerA,
            ["B"] = tickerB,
            ["C"] = tickerC
        };

        var result = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters { HighLowBars = 3 });

        Assert.Null(result.Series.NewHighs[0]);
        Assert.Null(result.Series.NewHighs[1]);
        Assert.Equal(1, result.Series.NewHighs[2]);
        Assert.Equal(1, result.Series.NewLows[2]);
        Assert.Equal(0, result.Series.NetNewHighs[2]); // 1 - 1 = 0
    }

    [Fact]
    public void McClellanOscillator_MatchesFastSlowEmaDifference()
    {
        // 45 days of candles so that McClellan Fast (19) and Slow (39) warm up (index 38 is first valid)
        var candlesA = new List<(string Date, decimal Close, decimal Volume)>();
        var candlesB = new List<(string Date, decimal Close, decimal Volume)>();

        var start = new DateTime(2024, 1, 1);
        for (int i = 0; i < 45; i++)
        {
            var d = start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            // Alternating pattern so A-D fluctuates
            decimal closeA = 100m + (i % 2 == 1 ? 5m : -2m);
            decimal closeB = 50m + (i % 3 == 0 ? 3m : -1m);
            candlesA.Add((d, closeA, 1000m));
            candlesB.Add((d, closeB, 1000m));
        }

        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = MakeSeries(candlesA.ToArray()),
            ["B"] = MakeSeries(candlesB.ToArray())
        };

        var result = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters
        {
            McClellanFast = 19,
            McClellanSlow = 39
        });

        Assert.Equal(45, result.DateCount);

        // First 38 bars (0 to 37) must be null
        for (int i = 0; i < 38; i++)
        {
            Assert.Null(result.Series.McClellan[i]);
            Assert.Null(result.Series.SummationIndex[i]);
        }

        // Bar 38 onwards must be valid
        Assert.NotNull(result.Series.McClellan[38]);
        Assert.NotNull(result.Series.SummationIndex[38]);
        Assert.Equal(result.Series.McClellan[38]!.Value, result.Series.SummationIndex[38]!.Value, Precision);

        // Summation index accumulation check
        double expectedSum = result.Series.McClellan[38]!.Value + result.Series.McClellan[39]!.Value;
        Assert.Equal(expectedSum, result.Series.SummationIndex[39]!.Value, Precision);
    }

    [Fact]
    public void OutputTailCount_SlicesAllSeriesCorrectly()
    {
        var tickerA = MakeSeries(
            ("2024-01-01", 100m, 1000m),
            ("2024-01-02", 105m, 1000m),
            ("2024-01-03", 110m, 1000m),
            ("2024-01-04", 115m, 1000m),
            ("2024-01-05", 120m, 1000m)
        );

        var universe = new Dictionary<string, IReadOnlyList<CandleData>> { ["A"] = tickerA };
        var result = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters { OutputTailCount = 2 });

        Assert.Equal(2, result.DateCount);
        Assert.Equal(new[] { "2024-01-04", "2024-01-05" }, result.Series.Dates);
        Assert.Equal(2, result.Series.Advancers.Count);
        Assert.Equal(2, result.Series.AdLine.Count);
    }

    [Fact]
    public void AsOfInvariance_AddingFutureBarsDoesNotChangePastResults()
    {
        // Proof (c): Historical breadths for Days 1..5 remain invariant when Day 6 is added
        var universeBase = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = MakeSeries(
                ("2024-01-01", 100m, 1000m),
                ("2024-01-02", 105m, 1200m),
                ("2024-01-03", 110m, 1500m),
                ("2024-01-04", 108m, 900m),
                ("2024-01-05", 112m, 1100m)
            ),
            ["B"] = MakeSeries(
                ("2024-01-01", 50m, 500m),
                ("2024-01-02", 48m, 600m),
                ("2024-01-03", 52m, 700m),
                ("2024-01-04", 51m, 400m),
                ("2024-01-05", 55m, 800m)
            )
        };

        var universeExtended = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = MakeSeries(
                ("2024-01-01", 100m, 1000m),
                ("2024-01-02", 105m, 1200m),
                ("2024-01-03", 110m, 1500m),
                ("2024-01-04", 108m, 900m),
                ("2024-01-05", 112m, 1100m),
                ("2024-01-06", 118m, 2000m) // future bar
            ),
            ["B"] = MakeSeries(
                ("2024-01-01", 50m, 500m),
                ("2024-01-02", 48m, 600m),
                ("2024-01-03", 52m, 700m),
                ("2024-01-04", 51m, 400m),
                ("2024-01-05", 55m, 800m),
                ("2024-01-06", 57m, 1200m) // future bar
            )
        };

        var resBase = MarketBreadthCalculator.Calculate(universeBase, new MarketBreadthParameters());
        var resExt = MarketBreadthCalculator.Calculate(universeExtended, new MarketBreadthParameters());

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(resBase.Series.Advancers[i], resExt.Series.Advancers[i]);
            Assert.Equal(resBase.Series.Decliners[i], resExt.Series.Decliners[i]);
            Assert.Equal(resBase.Series.Unchanged[i], resExt.Series.Unchanged[i]);
            Assert.Equal(resBase.Series.AdLine[i], resExt.Series.AdLine[i]);
            Assert.Equal(resBase.Series.Cvi[i], resExt.Series.Cvi[i]);
        }
    }

    [Fact]
    public void ExcludedSymbols_RecordsTickerWithFewerThanTwoCandles()
    {
        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["GOOD"] = MakeSeries(("2024-01-01", 100m, 100m), ("2024-01-02", 105m, 100m)),
            ["SHORT"] = MakeSeries(("2024-01-01", 100m, 100m)) // Only 1 candle
        };

        var result = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters());

        Assert.Equal(1, result.SymbolCount);
        Assert.Single(result.ExcludedSymbols);
        Assert.Equal("SHORT", result.ExcludedSymbols[0].Symbol);
    }
}
