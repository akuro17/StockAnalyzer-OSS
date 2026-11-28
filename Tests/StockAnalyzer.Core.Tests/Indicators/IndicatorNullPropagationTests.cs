using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Indicators.Volume;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class IndicatorNullPropagationTests
{
    private static List<CoreCandleData> BuildWaveCandles(int count, decimal basePrice = 100m, decimal amplitude = 8m)
    {
        var start = new DateTime(2025, 1, 1);
        var candles = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal c = basePrice + amplitude * (decimal)Math.Sin(i * 0.25) + i * 0.1m;
            candles.Add(new CoreCandleData(start.AddDays(i), c - 0.5m, c + 1.5m, c - 1.5m, c, 1000 + i * 10));
        }
        return candles;
    }

    private static List<decimal?> BuildSeriesWithLeadingAndInteriorNulls(int totalLength, int leadingNulls, int interiorNullIndex, int? secondNullIndex = null)
    {
        var series = new List<decimal?>(totalLength);
        for (int i = 0; i < totalLength; i++)
        {
            if (i < leadingNulls || i == interiorNullIndex || (secondNullIndex.HasValue && i == secondNullIndex.Value))
            {
                series.Add(null);
            }
            else
            {
                series.Add(100m + 5m * (decimal)Math.Sin(i * 0.3) + i * 0.2m);
            }
        }
        return series;
    }

    [Fact]
    public void PartA_RollingWindowIndicators_PropagateLeadingAndInteriorNullsWithoutZeroSpike()
    {
        // Constant 100m series with leading nulls [0..1] and interior null at index 8
        var series = new List<decimal?>
        {
            null, null,
            100m, 100m, 100m, 100m, 100m, 100m,
            null,
            100m, 100m, 100m, 100m, 100m, 100m
        };

        var stdDev = new CoreStandardDeviationIndicator { Period = 3 };
        var stdRes = stdDev.CalculateSeries(series);
        Assert.Null(stdRes.MainValues[0]);
        Assert.Null(stdRes.MainValues[2]);
        Assert.Null(stdRes.MainValues[8]);
        Assert.Null(stdRes.MainValues[9]);
        Assert.Null(stdRes.MainValues[10]);
        Assert.Equal(0m, stdRes.MainValues[5]);
        Assert.Equal(0m, stdRes.MainValues[11]);

        var bandWidth = new CoreBandWidthIndicator { Period = 3 };
        var bwRes = bandWidth.CalculateSeries(series);
        Assert.Null(bwRes.MainValues[8]);
        Assert.Null(bwRes.MainValues[10]);
        Assert.Equal(0m, bwRes.MainValues[5]);
        Assert.Equal(0m, bwRes.MainValues[11]);

        var bbRatio = new CoreBollingerBandsRatioIndicator { Period = 3 };
        var bbRes = bbRatio.CalculateSeries(series);
        Assert.Null(bbRes.MainValues[8]);
        Assert.Null(bbRes.MainValues[10]);
        Assert.Equal(0.5m, bbRes.MainValues[5]);
        Assert.Equal(0.5m, bbRes.MainValues[11]);

        var zScore = new CoreZScoreIndicator { Period = 3 };
        var zRes = zScore.CalculateSeries(series);
        Assert.Null(zRes.MainValues[8]);
        Assert.Null(zRes.MainValues[10]);
        Assert.Equal(0m, zRes.MainValues[5]);
        Assert.Equal(0m, zRes.MainValues[11]);

        var ulcer = new CoreUlcerIndexIndicator { Period = 3 };
        var uRes = ulcer.CalculateSeries(series);
        Assert.Null(uRes.MainValues[8]);
        Assert.Null(uRes.MainValues[10]);
        Assert.Equal(0m, uRes.MainValues[5]);
        Assert.Equal(0m, uRes.MainValues[11]);
    }

    [Fact]
    public void PartA_DifferenceAndEmaMomentumIndicators_PropagateNullsWithoutZeroSpike()
    {
        var series = new List<decimal?>
        {
            null, null,
            100m, 102m, 104m, 106m, 108m, 110m,
            null,
            112m, 114m, 116m, 118m, 120m
        };

        var mom = new CoreMomentumIndicator { Period = 2 };
        var momRes = mom.CalculateSeries(series);
        Assert.Null(momRes.MainValues[2]);
        Assert.Null(momRes.MainValues[3]);
        Assert.Equal(4m, momRes.MainValues[4]);
        Assert.Null(momRes.MainValues[8]);
        Assert.Null(momRes.MainValues[10]);
        Assert.Equal(4m, momRes.MainValues[11]);

        var roc = new CoreRocIndicator { Period = 2 };
        var rocRes = roc.CalculateSeries(series);
        Assert.Null(rocRes.MainValues[8]);
        Assert.Null(rocRes.MainValues[10]);
        Assert.NotNull(rocRes.MainValues[11]);

        // Preserve explicit non-null prev==0m -> 0m contract
        var zeroBaseRoc = new CoreRocIndicator { Period = 1 };
        var zeroRocRes = zeroBaseRoc.CalculateSeries(new decimal?[] { 0m, 50m });
        Assert.Equal(0m, zeroRocRes.MainValues[1]);

        var cmo = new CoreCmoIndicator { Period = 2 };
        var cmoRes = cmo.CalculateSeries(series);
        Assert.Null(cmoRes.MainValues[8]);
        Assert.Null(cmoRes.MainValues[9]);
        Assert.Null(cmoRes.MainValues[10]);
        Assert.Equal(100m, cmoRes.MainValues[11]);

        var ppo = new CorePpoIndicator { FastPeriod = 2, SlowPeriod = 3 };
        var ppoRes = ppo.CalculateSeries(series);
        Assert.Null(ppoRes.MainValues[0]);
        Assert.Null(ppoRes.MainValues[3]);
        Assert.Null(ppoRes.MainValues[8]);
        Assert.NotNull(ppoRes.MainValues[9]);
        Assert.True(Math.Abs(ppoRes.MainValues[9]!.Value) < 20m);
    }

    [Fact]
    public void PartA_CoppockConnorsRsiAndKeltner_HandleNullsAndCandlesCleanly()
    {
        var longSeries = BuildSeriesWithLeadingAndInteriorNulls(140, leadingNulls: 5, interiorNullIndex: 120);

        var coppock = new CoreCoppockCurveIndicator { LongRocPeriod = 4, ShortRocPeriod = 2, WmaPeriod = 3 };
        var coppockRes = coppock.CalculateSeries(longSeries);
        Assert.Null(coppockRes.MainValues[120]);
        Assert.Null(coppockRes.MainValues[122]);
        Assert.Null(coppockRes.MainValues[124]);
        Assert.NotNull(coppockRes.MainValues[130]);

        var connors = new CoreConnorsRsiIndicator { RsiPeriod = 3, StreakPeriod = 2, PercentRankPeriod = 10 };
        var connorsRes = connors.CalculateSeries(longSeries);
        for (int i = 0; i <= 14; i++)
        {
            Assert.Null(connorsRes.MainValues[i]);
        }
        Assert.Null(connorsRes.MainValues[120]);
        Assert.Null(connorsRes.MainValues[125]);
        Assert.NotNull(connorsRes.MainValues[139]);

        var candles = BuildWaveCandles(40);
        var keltner = new CoreKeltnerChannelIndicator { EmaPeriod = 10, AtrPeriod = 10, Multiplier = 2m };
        var keltnerRes = keltner.Calculate(candles);
        Assert.True(keltnerRes.IsSuccessful);
        for (int i = 0; i < 10; i++)
        {
            Assert.Null(keltner.Values[i]);
            Assert.Null(keltner.UpperBand[i]);
            Assert.Null(keltner.LowerBand[i]);
        }
        for (int i = 10; i < candles.Count; i++)
        {
            Assert.NotNull(keltner.Values[i]);
            Assert.True(keltner.UpperBand[i] > keltner.Values[i]);
            Assert.True(keltner.LowerBand[i] < keltner.Values[i]);
        }
    }

    [Fact]
    public void PartB_ChainedSma20IntoDemaSsaHilbertAndObv_PropagatesLeadingAndInteriorNulls()
    {
        var candles = BuildWaveCandles(160);
        var sma = new CoreSmaIndicator { Period = 20 };
        var smaResult = sma.Calculate(candles);
        var chained = smaResult.MainValues.ToList();

        // Inject an interior null in the post-warmup chain
        chained[90] = null;

        // 1. DEMA chained from SMA(20)
        var dema = new CoreDemaIndicator { Period = 10 };
        var demaRes = dema.CalculateSeries(chained);
        Assert.True(demaRes.IsSuccessful);
        Assert.Equal(chained.Count, demaRes.MainValues.Count);
        for (int i = 0; i < 19; i++) Assert.Null(demaRes.MainValues[i]);
        Assert.Null(demaRes.MainValues[90]);
        Assert.NotNull(demaRes.MainValues[89]);
        Assert.InRange(demaRes.MainValues[159]!.Value, 80m, 130m);

        // 2. SSA chained from SMA(20)
        var ssa = new CoreSSAIndicator { WindowSize = 16, EmbeddingDimension = 6, NumComponents = 2 };
        var ssaRes = ssa.CalculateSeries(chained);
        Assert.True(ssaRes.IsSuccessful);
        Assert.Equal(chained.Count, ssaRes.MainValues.Count);
        for (int i = 0; i < 19 + 15; i++) Assert.Null(ssaRes.MainValues[i]);
        for (int i = 90; i < 90 + 16; i++) Assert.Null(ssaRes.MainValues[i]);
        Assert.NotNull(ssaRes.MainValues[89]);
        Assert.NotNull(ssaRes.MainValues[159]);
        Assert.InRange(ssaRes.MainValues[159]!.Value, 80m, 130m);

        // 3. HilbertTransform chained from SMA(20)
        var ht = new CoreHilbertTransformIndicator();
        var htRes = ht.CalculateSeries(chained);
        Assert.True(htRes.IsSuccessful);
        Assert.Equal(chained.Count, htRes.MainValues.Count);
        int warmup = IndicatorDefaultConstants.HilbertTransformWarmupBars;
        for (int i = 0; i < 19 + warmup; i++) Assert.Null(htRes.MainValues[i]);
        for (int i = 90; i < Math.Min(chained.Count, 90 + warmup); i++) Assert.Null(htRes.MainValues[i]);
        Assert.NotNull(htRes.MainValues[89]);
        Assert.NotNull(htRes.MainValues[159]);

        // 4. OBV chained via CalculateSeries
        var obv = new CoreObvIndicator();
        var obvRes = obv.CalculateSeries(chained);
        Assert.True(obvRes.IsSuccessful);
        Assert.Equal(chained.Count, obvRes.MainValues.Count);
        for (int i = 0; i <= 19; i++) Assert.Null(obvRes.MainValues[i]);
        Assert.NotNull(obvRes.MainValues[20]);
        Assert.Null(obvRes.MainValues[90]);
        Assert.Null(obvRes.MainValues[91]);
        Assert.NotNull(obvRes.MainValues[92]);
    }

    [Fact]
    public void PartB_MovingAveragesAndTrendIndicators_PropagateNullsWithoutZeroFabrication()
    {
        var series = BuildSeriesWithLeadingAndInteriorNulls(80, leadingNulls: 5, interiorNullIndex: 40);

        var bsma = new CoreBSmaIndicator { Period = 5 };
        var bsmaRes = bsma.CalculateSeries(series);
        Assert.Null(bsmaRes.MainValues[40]);
        Assert.Null(bsmaRes.MainValues[44]);
        Assert.NotNull(bsmaRes.MainValues[39]);
        Assert.NotNull(bsmaRes.MainValues[45]);
        Assert.True(bsmaRes.MainValues[45]!.Value > 50m);

        var gma = new CoreGmaIndicator();
        var gmaRes = gma.CalculateSeries(series);
        Assert.True(gmaRes.IsSuccessful);
        var ema3 = gmaRes.GetSeries("EMA3");
        Assert.Null(ema3[4]);
        Assert.Null(ema3[40]);
        Assert.True(ema3[41]!.Value > 50m);

        var jma = new CoreJmaIndicator { Period = 5 };
        var jmaRes = jma.CalculateSeries(series);
        Assert.Null(jmaRes.MainValues[40]);
        Assert.True(jmaRes.MainValues[41]!.Value > 50m);

        var kalman = new CoreKalmanFilterIndicator();
        var kalmanRes = kalman.CalculateSeries(series);
        Assert.Null(kalmanRes.MainValues[0]);
        Assert.Equal(series[5], kalmanRes.MainValues[5]);
        Assert.Null(kalmanRes.MainValues[40]);
        Assert.True(kalmanRes.MainValues[41]!.Value > 50m);

        var mtf = new CoreMtfLaggedEmaIndicator { Period = 3, TimeFrameMultiplier = 2, Lag = 1 };
        var mtfRes = mtf.CalculateSeries(series);
        Assert.Null(mtfRes.MainValues[0]);
        Assert.NotNull(mtfRes.MainValues[79]);

        var vama = new CoreVamaIndicator { Period = 4 };
        var vamaRes = vama.CalculateSeries(series);
        Assert.Null(vamaRes.MainValues[40]);
        Assert.Null(vamaRes.MainValues[43]);
        Assert.NotNull(vamaRes.MainValues[55]);

        var zlema = new CoreZlemaIndicator { Period = 5 };
        var zlemaRes = zlema.CalculateSeries(series);
        Assert.Null(zlemaRes.MainValues[40]);
        Assert.Null(zlemaRes.MainValues[42]);
        Assert.True(zlemaRes.MainValues[45]!.Value > 50m);

        var dma = new CoreDmaIndicator { Period = 5, Displacement = 2 };
        var dmaRes = dma.CalculateSeries(series);
        Assert.Null(dmaRes.MainValues[42]);
        Assert.Null(dmaRes.MainValues[46]);
        Assert.NotNull(dmaRes.MainValues[47]);

        var env = new CoreEnvelopeIndicator { Period = 5 };
        var envRes = env.CalculateSeries(series);
        Assert.Null(envRes.MainValues[40]);
        Assert.Null(envRes.GetSeries("Upper")[40]);
        Assert.Null(envRes.GetSeries("Lower")[40]);
        Assert.NotNull(envRes.GetSeries("Upper")[45]);

        var maCross = new CoreMovingAverageCrossIndicator { ShortPeriod = 3, LongPeriod = 5 };
        var maCrossRes = maCross.CalculateSeries(series);
        Assert.True(maCrossRes.IsSuccessful);
        Assert.Null(maCross.ShortMA[40]);
        Assert.Null(maCross.LongMA[40]);

        var wilder = new CoreWilderSmoothingIndicator { Period = 5 };
        var wilderRes = wilder.CalculateSeries(series);
        Assert.Null(wilderRes.MainValues[40]);
        Assert.True(wilderRes.MainValues[41]!.Value > 50m);
    }

    [Fact]
    public void PartB_SpectralHmmAndElderImpulseIndicators_PropagateNullsAcrossSegments()
    {
        var series = BuildSeriesWithLeadingAndInteriorNulls(170, leadingNulls: 5, interiorNullIndex: 85);

        var elderImpulse = new CoreElderImpulseIndicator { EmaPeriod = 5, MacdFastPeriod = 4, MacdSlowPeriod = 8, MacdSignalPeriod = 3 };
        var eiRes = elderImpulse.CalculateSeries(series);
        Assert.Null(eiRes.MainValues[85]);
        Assert.Null(eiRes.MainValues[86]);
        Assert.NotNull(eiRes.MainValues[100]);

        var ssaAnomaly = new CoreSSAAnomalyIndicator { WindowSize = 16, EmbeddingDimension = 6, NumComponents = 2, AutoRank = false };
        var saRes = ssaAnomaly.CalculateSeries(series);
        Assert.Null(saRes.MainValues[85]);
        Assert.Null(ssaAnomaly.AnomalyStateSeries[85]);
        Assert.Null(saRes.MainValues[99]);
        Assert.NotNull(saRes.MainValues[120]);

        var ssaCycle = new CoreSSACycleIndicator { WindowSize = 16, EmbeddingDimension = 6 };
        var scRes = ssaCycle.CalculateSeries(series);
        Assert.True(scRes.IsSuccessful);
        Assert.Null(scRes.MainValues[85]);
        Assert.Null(ssaCycle.DominantPeriod[85]);

        var ssaEntropy = new CoreSSAEntropyIndicator { WindowSize = 16, EmbeddingDimension = 6 };
        var seRes = ssaEntropy.CalculateSeries(series);
        Assert.Null(seRes.MainValues[85]);
        Assert.Null(seRes.MainValues[99]);
        Assert.NotNull(seRes.MainValues[120]);

        var ssaSnr = new CoreSSASNRIndicator { WindowSize = 16, EmbeddingDimension = 6, NumComponents = 2 };
        var snrRes = ssaSnr.CalculateSeries(series);
        Assert.Null(snrRes.MainValues[85]);
        Assert.Null(ssaSnr.SignalPurity[85]);
        Assert.Null(snrRes.MainValues[99]);
        Assert.NotNull(snrRes.MainValues[120]);

        var ssaBand = new CoreSSAResidualBandIndicator { WindowSize = 16, EmbeddingDimension = 6, NumComponents = 2 };
        var sbRes = ssaBand.CalculateSeries(series);
        Assert.Null(sbRes.MainValues[85]);
        Assert.Null(ssaBand.UpperBand[85]);
        Assert.Null(ssaBand.LowerBand[85]);
        Assert.Null(ssaBand.BandWidth[85]);
        Assert.NotNull(sbRes.MainValues[120]);

        var hmm = new CoreHmmIndicator { Period = 15, States = 2 };
        var hmmRes = hmm.CalculateSeries(series);
        Assert.Null(hmmRes.MainValues[0]);
        Assert.Null(hmmRes.MainValues[85]);
        Assert.Null(hmmRes.MainValues[99]);
        Assert.NotNull(hmmRes.MainValues[120]);

        var htTrendline = new CoreHilbertTrendlineIndicator();
        var httRes = htTrendline.CalculateSeries(series);
        Assert.Null(httRes.MainValues[85]);
        Assert.NotNull(httRes.MainValues[160]);

        var htSine = new CoreHilbertSineIndicator();
        var hsRes = htSine.CalculateSeries(series);
        Assert.Null(hsRes.MainValues[85]);
        Assert.Null(htSine.Sine[85]);
        Assert.Null(htSine.LeadSine[85]);
        Assert.NotNull(hsRes.MainValues[160]);

        var htMode = new CoreHilbertTrendModeIndicator();
        var hmRes = htMode.CalculateSeries(series);
        Assert.Null(hmRes.MainValues[85]);
        Assert.NotNull(hmRes.MainValues[160]);

        var polarFreq = new CorePolarCycleAngularFrequencyIndicator();
        var pfRes = polarFreq.CalculateSeries(series);
        Assert.Null(pfRes.MainValues[85]);
        Assert.Null(polarFreq.PhaseAngularVelocity[85]);
        Assert.NotNull(pfRes.MainValues[160]);

        var polarPhase = new CorePolarPhaseIndicator();
        var ppRes = polarPhase.CalculateSeries(series);
        Assert.Null(ppRes.MainValues[85]);
        Assert.Null(polarPhase.NormalizedInPhase[85]);
        Assert.NotNull(ppRes.MainValues[160]);
    }
}
