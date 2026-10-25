using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.ZeroAllocation;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Service interface for calculating Heikin-Ashi and Non-Time-Series chart series without GUI coupling.
/// </summary>
public interface IChartSeriesCalculationService
{
    /// <summary>
    /// Calculates the series and metrics for <paramref name="request"/> on <paramref name="candles"/>.
    /// </summary>
    ChartSeriesCalculationResult Calculate(
        IReadOnlyList<CoreCandleData> candles,
        ChartSeriesRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stateless implementation delegating to the single source of truth utilities in <see cref="StockAnalyzer.Core"/>.
/// </summary>
public sealed class ChartSeriesCalculationService : IChartSeriesCalculationService
{
    /// <summary>
    /// Upper bound on converted elements to guard against runaway memory usage / CPU hangs from tiny box sizes.
    /// </summary>
    public const int MaxGeneratedElements = 50_000;

    public ChartSeriesCalculationResult Calculate(
        IReadOnlyList<CoreCandleData> candles,
        ChartSeriesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        return request.ChartType switch
        {
            ChartType.HeikinAshi => CalculateHeikinAshi(candles, request, cancellationToken),
            ChartType.Renko => CalculateRenko(candles, request, cancellationToken),
            ChartType.Kagi => CalculateKagi(candles, request, cancellationToken),
            ChartType.PointAndFigure => CalculatePointAndFigure(candles, request, cancellationToken),
            ChartType.ThreeLineBreak => CalculateThreeLineBreak(candles, request, cancellationToken),
            _ => throw new NotSupportedException($"ChartType {request.ChartType} is not supported by {nameof(ChartSeriesCalculationService)}.")
        };
    }

    private static void EnsureElementCountWithinBounds(IReadOnlyList<CoreCandleData> candles, decimal effectiveSize)
    {
        if (effectiveSize <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveSize), "Effective box/brick size must be positive.");
        }

        decimal totalVariation = 0m;
        for (int i = 0; i < candles.Count; i++)
        {
            decimal barRange = candles[i].High - candles[i].Low;
            if (barRange > 0m)
            {
                totalVariation += barRange;
            }
            if (i > 0)
            {
                totalVariation += Math.Abs(candles[i].Close - candles[i - 1].Close);
            }
        }

        if (totalVariation / effectiveSize > MaxGeneratedElements)
        {
            decimal minRequired = Math.Ceiling((totalVariation / MaxGeneratedElements) * 10000m) / 10000m;
            throw new InvalidOperationException(
                $"Effective box/brick size ({effectiveSize}) is too small for the symbol's price variation ({totalVariation}), " +
                $"which would generate more than {MaxGeneratedElements} elements. Minimum recommended size is at least {minRequired}.");
        }
    }

    private static ChartSeriesCalculationResult CalculateHeikinAshi(
        IReadOnlyList<CoreCandleData> candles,
        ChartSeriesRequest request,
        CancellationToken cancellationToken)
    {
        if (candles.Count == 0)
        {
            return new ChartSeriesCalculationResult(ChartType.HeikinAshi, 0, 0, null, null, Array.Empty<ChartSeriesElement>(), null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var haBuffer = new List<CoreCandleData>(candles.Count);
        HeikinAshiConverter.Convert(candles, haBuffer);

        var elements = new List<ChartSeriesElement>(haBuffer.Count);
        for (int i = 0; i < haBuffer.Count; i++)
        {
            var b = haBuffer[i];
            string dir = b.Close >= b.Open ? "Bullish" : "Bearish";
            elements.Add(new ChartSeriesElement(i, b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume, dir));
        }

        return new ChartSeriesCalculationResult(ChartType.HeikinAshi, candles.Count, elements.Count, null, null, elements, null, null);
    }

    private static ChartSeriesCalculationResult CalculateRenko(
        IReadOnlyList<CoreCandleData> candles,
        ChartSeriesRequest request,
        CancellationToken cancellationToken)
    {
        if (candles.Count == 0)
        {
            return new ChartSeriesCalculationResult(ChartType.Renko, 0, 0, null, null, Array.Empty<ChartSeriesElement>(), null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        int reversal = request.Reversal > 0 ? request.Reversal : 2;
        decimal effectiveSize = AutoBoxSizeCalculator.Calculate(
            request.SizingMode,
            candles,
            request.ManualSize,
            request.AtrPeriod,
            request.AtrMultiplier,
            request.RoundingMode,
            request.FallbackMode,
            candles[^1].Close,
            request.Percentage);

        EnsureElementCountWithinBounds(candles, effectiveSize);

        ZeroAllocCandleData[] rent = ArrayPool<ZeroAllocCandleData>.Shared.Rent(candles.Count);
        try
        {
            for (int i = 0; i < candles.Count; i++)
            {
                var c = candles[i];
                rent[i] = new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var adapter = RenkoCalculator.Calculate(rent.AsMemory(0, candles.Count), new RenkoParameters(effectiveSize, reversal, request.RoundingMode));
            int count = adapter.Count;

            var renkoSeries = new List<CoreCandleData>(count);
            var times = adapter.Timestamps.Span;
            var opens = adapter.Opens.Span;
            var highs = adapter.Highs.Span;
            var lows = adapter.Lows.Span;
            var closes = adapter.Closes.Span;
            var vols = adapter.Volumes.Span;

            for (int i = 0; i < count; i++)
            {
                renkoSeries.Add(new CoreCandleData(times[i], opens[i], highs[i], lows[i], closes[i], vols[i]));
            }

            var elements = new List<ChartSeriesElement>(count);
            for (int i = 0; i < count; i++)
            {
                var b = renkoSeries[i];
                bool isUp = b.Close >= b.Open;
                string dir = isUp ? "Up" : "Down";

                int runStart = i;
                while (runStart > 0 && (renkoSeries[runStart - 1].Close >= renkoSeries[runStart - 1].Open) == isUp)
                {
                    runStart--;
                }
                int runCount = i - runStart + 1;

                decimal brickHigh = Math.Max(b.Open, b.Close);
                decimal brickLow = Math.Min(b.Open, b.Close);
                decimal reversalPrice = isUp ? brickLow - (reversal * effectiveSize) : brickHigh + (reversal * effectiveSize);
                decimal nextBrickPrice = isUp ? brickHigh + effectiveSize : brickLow - effectiveSize;

                var trendSpan = TrendDates.Describe(renkoSeries, null, i, ChartType.Renko);

                elements.Add(new ChartSeriesElement(
                    Index: i,
                    Timestamp: b.Timestamp,
                    Open: b.Open,
                    High: b.High,
                    Low: b.Low,
                    Close: b.Close,
                    Volume: b.Volume,
                    Direction: dir,
                    RunCount: runCount,
                    ReversalPrice: reversalPrice,
                    NextTargetPrice: nextBrickPrice,
                    ReversalDate: trendSpan.ReversalDate,
                    UpdateDate: trendSpan.UpdateDate,
                    TrendDays: trendSpan.Days));
            }

            IReadOnlyList<MultiWaveSignal>? mwSignals = null;
            if (request.IncludeMultiWave && renkoSeries.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mw = new MultiWavePatternEngine();
                foreach (var b in renkoSeries)
                {
                    mw.ProcessNextBlock(b);
                }
                mwSignals = mw.Signals.ToArray();
            }

            return new ChartSeriesCalculationResult(ChartType.Renko, candles.Count, elements.Count, effectiveSize, null, elements, mwSignals, null);
        }
        finally
        {
            ArrayPool<ZeroAllocCandleData>.Shared.Return(rent, clearArray: true);
        }
    }

    private static ChartSeriesCalculationResult CalculateKagi(
        IReadOnlyList<CoreCandleData> candles,
        ChartSeriesRequest request,
        CancellationToken cancellationToken)
    {
        if (candles.Count == 0)
        {
            return new ChartSeriesCalculationResult(ChartType.Kagi, 0, 0, null, null, Array.Empty<ChartSeriesElement>(), null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        decimal effectiveSize = AutoBoxSizeCalculator.Calculate(
            request.SizingMode,
            candles,
            request.ManualSize,
            request.AtrPeriod,
            request.AtrMultiplier,
            request.RoundingMode,
            request.FallbackMode,
            candles[^1].Close,
            request.Percentage);

        EnsureElementCountWithinBounds(candles, effectiveSize);

        ZeroAllocCandleData[] rent = ArrayPool<ZeroAllocCandleData>.Shared.Rent(candles.Count);
        try
        {
            for (int i = 0; i < candles.Count; i++)
            {
                var c = candles[i];
                rent[i] = new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var adapter = KagiCalculator.Calculate(rent.AsMemory(0, candles.Count), new KagiParameters(effectiveSize, request.RoundingMode));
            int count = adapter.Count;

            var kagiSeries = new List<CoreCandleData>(count);
            var kagiDates = new List<ElementDates>(count);
            var times = adapter.Timestamps.Span;
            var opens = adapter.Opens.Span;
            var highs = adapter.Highs.Span;
            var lows = adapter.Lows.Span;
            var closes = adapter.Closes.Span;
            var vols = adapter.Volumes.Span;
            var dates = adapter.ElementDates.Span;

            for (int i = 0; i < count; i++)
            {
                kagiSeries.Add(new CoreCandleData(times[i], opens[i], highs[i], lows[i], closes[i], vols[i]));
                kagiDates.Add(dates[i]);
            }

            var colMapBuffer = new List<int>(count);
            KagiColumns.BuildColumnMap(kagiSeries, request.InitialColumn, colMapBuffer);
            int totalColumns = count > 0 ? colMapBuffer[^1] - request.InitialColumn + 1 : 0;

            var elements = new List<ChartSeriesElement>(count);
            for (int i = 0; i < count; i++)
            {
                var b = kagiSeries[i];
                bool isUp = b.Close >= b.Open;
                string dir = isUp ? "Up" : "Down";
                bool isYang = b.Volume == 1L;
                string lineState = isYang ? "Yang" : "Yin";
                int colIdx = colMapBuffer[i];

                var (colStart, colEnd) = KagiColumns.ColumnBounds(kagiSeries, i);
                decimal colSpan = Math.Abs(kagiSeries[colEnd].Close - kagiSeries[colStart].Open);
                decimal revPrice = isUp ? b.Close - effectiveSize : b.Close + effectiveSize;

                var trendSpan = TrendDates.Describe(kagiSeries, kagiDates, i, ChartType.Kagi);

                elements.Add(new ChartSeriesElement(
                    Index: i,
                    Timestamp: b.Timestamp,
                    Open: b.Open,
                    High: b.High,
                    Low: b.Low,
                    Close: b.Close,
                    Volume: b.Volume,
                    Direction: dir,
                    ColumnIndex: colIdx,
                    IsYang: isYang,
                    LineState: lineState,
                    ColumnSpan: colSpan,
                    ReversalPrice: revPrice,
                    StartDate: kagiDates[i].Start,
                    EndDate: kagiDates[i].End,
                    ReversalDate: trendSpan.ReversalDate,
                    UpdateDate: trendSpan.UpdateDate,
                    TrendDays: trendSpan.Days));
            }

            IReadOnlyList<MultiWaveSignal>? mwSignals = null;
            if (request.IncludeMultiWave && kagiSeries.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mw = new MultiWavePatternEngine();
                foreach (var b in kagiSeries)
                {
                    mw.ProcessNextBlock(b);
                }
                mwSignals = mw.Signals.ToArray();
            }

            return new ChartSeriesCalculationResult(ChartType.Kagi, candles.Count, elements.Count, effectiveSize, totalColumns, elements, mwSignals, null);
        }
        finally
        {
            ArrayPool<ZeroAllocCandleData>.Shared.Return(rent, clearArray: true);
        }
    }

    private static ChartSeriesCalculationResult CalculatePointAndFigure(
        IReadOnlyList<CoreCandleData> candles,
        ChartSeriesRequest request,
        CancellationToken cancellationToken)
    {
        if (candles.Count == 0)
        {
            return new ChartSeriesCalculationResult(ChartType.PointAndFigure, 0, 0, null, null, Array.Empty<ChartSeriesElement>(), null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        int reversal = request.Reversal > 0 ? request.Reversal : ChartConstants.DefaultReversalAmount;
        decimal effectiveSize = AutoBoxSizeCalculator.Calculate(
            request.SizingMode,
            candles,
            request.ManualSize,
            request.AtrPeriod,
            request.AtrMultiplier,
            request.RoundingMode,
            request.FallbackMode,
            candles[^1].Close,
            request.Percentage);

        EnsureElementCountWithinBounds(candles, effectiveSize);

        var pnfSeries = new List<CoreCandleData>();
        var pnfDates = new List<ElementDates>();
        PointAndFigureConverter.Convert(candles, effectiveSize, reversal, pnfSeries, request.RoundingMode, pnfDates);

        int count = pnfSeries.Count;
        var elements = new List<ChartSeriesElement>(count);
        for (int i = 0; i < count; i++)
        {
            var b = pnfSeries[i];
            bool isX = b.Close >= b.Open;
            string colType = isX ? "X" : "O";
            string dir = isX ? "Up" : "Down";
            int boxCount = effectiveSize > 0m ? Math.Max(1, (int)Math.Round((b.High - b.Low) / effectiveSize, MidpointRounding.AwayFromZero) + 1) : 1;
            decimal revPrice = isX ? b.High - (reversal * effectiveSize) : b.Low + (reversal * effectiveSize);
            decimal nextBox = isX ? b.High + effectiveSize : b.Low - effectiveSize;

            var trendSpan = TrendDates.Describe(pnfSeries, pnfDates, i, ChartType.PointAndFigure);

            elements.Add(new ChartSeriesElement(
                Index: i,
                Timestamp: b.Timestamp,
                Open: b.Open,
                High: b.High,
                Low: b.Low,
                Close: b.Close,
                Volume: b.Volume,
                Direction: dir,
                ColumnType: colType,
                BoxCount: boxCount,
                ReversalPrice: revPrice,
                NextTargetPrice: nextBox,
                StartDate: pnfDates[i].Start,
                EndDate: pnfDates[i].End,
                ReversalDate: trendSpan.ReversalDate,
                UpdateDate: trendSpan.UpdateDate,
                TrendDays: trendSpan.Days));
        }

        PnfAnalysisResult? pnfAnalysis = null;
        if (request.IncludePatterns && pnfSeries.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullAnalysis = PointAndFigurePatternEngine.Analyze(pnfSeries, effectiveSize);
            var filteredSignals = fullAnalysis.Signals.Where(s => s.PatternType switch
            {
                PnfPatternType.DoubleTopBreakout or PnfPatternType.DoubleBottomBreakout => request.ShowDoubleBreakout,
                PnfPatternType.TripleTopBreakout or PnfPatternType.TripleBottomBreakout => request.ShowTripleBreakout,
                PnfPatternType.BullishSupportLineBreakout or PnfPatternType.BearishResistanceLineBreakout => request.ShowTrendlineBreakout,
                PnfPatternType.BullishTriangleBreakout or PnfPatternType.BearishTriangleBreakout => request.ShowTriangleBreakout,
                PnfPatternType.BullishCatapult or PnfPatternType.BearishCatapult => request.ShowCatapultBreakout,
                _ => true
            }).ToList();

            var activeLines = request.ShowTrendlineBreakout ? fullAnalysis.ActiveLines : Array.Empty<PnfTrendline>();
            pnfAnalysis = new PnfAnalysisResult(filteredSignals, activeLines);
        }

        IReadOnlyList<MultiWaveSignal>? mwSignals = null;
        if (request.IncludeMultiWave && pnfSeries.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mw = new MultiWavePatternEngine();
            foreach (var b in pnfSeries)
            {
                mw.ProcessNextBlock(b);
            }
            mwSignals = mw.Signals.ToArray();
        }

        return new ChartSeriesCalculationResult(ChartType.PointAndFigure, candles.Count, elements.Count, effectiveSize, null, elements, mwSignals, pnfAnalysis);
    }

    private static ChartSeriesCalculationResult CalculateThreeLineBreak(
        IReadOnlyList<CoreCandleData> candles,
        ChartSeriesRequest request,
        CancellationToken cancellationToken)
    {
        if (candles.Count == 0)
        {
            return new ChartSeriesCalculationResult(ChartType.ThreeLineBreak, 0, 0, null, null, Array.Empty<ChartSeriesElement>(), null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        int lineCount = request.LineCount > 0 ? request.LineCount : ChartConstants.DefaultThreeLineBreakLineCount;
        var blockBuffer = new List<ThreeLineBreakBlock>();
        ThreeLineBreakConverter.Convert(candles, lineCount, request.Tolerance, request.MinimumMove, blockBuffer);

        int count = blockBuffer.Count;
        var tlbSeries = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            var blk = blockBuffer[i];
            tlbSeries.Add(new CoreCandleData(blk.EndDate, blk.OpenPrice, blk.High, blk.Low, blk.ClosePrice, 0));
        }

        var elements = new List<ChartSeriesElement>(count);
        for (int i = 0; i < count; i++)
        {
            var blk = blockBuffer[i];
            bool isUp = blk.IsUp;
            string dir = isUp ? "Up" : "Down";

            int runCount = 1;
            for (int k = i - 1; k >= 0; k--)
            {
                if (blockBuffer[k].IsUp == isUp) runCount++;
                else break;
            }

            int lookbackStart = Math.Max(0, i - lineCount + 1);
            decimal revPrice;
            if (isUp)
            {
                decimal revLow = decimal.MaxValue;
                for (int b = lookbackStart; b <= i; b++)
                {
                    if (blockBuffer[b].Low < revLow) revLow = blockBuffer[b].Low;
                }
                revPrice = request.MinimumMove > 0m ? Math.Min(revLow, blk.ClosePrice - request.MinimumMove) : revLow;
            }
            else
            {
                decimal revHigh = decimal.MinValue;
                for (int b = lookbackStart; b <= i; b++)
                {
                    if (blockBuffer[b].High > revHigh) revHigh = blockBuffer[b].High;
                }
                revPrice = request.MinimumMove > 0m ? Math.Max(revHigh, blk.ClosePrice + request.MinimumMove) : revHigh;
            }

            decimal nextExt = isUp ? blk.High + request.MinimumMove : blk.Low - request.MinimumMove;
            var trendSpan = TrendDates.Describe(tlbSeries, null, i, ChartType.ThreeLineBreak);

            elements.Add(new ChartSeriesElement(
                Index: i,
                Timestamp: blk.EndDate,
                Open: blk.OpenPrice,
                High: blk.High,
                Low: blk.Low,
                Close: blk.ClosePrice,
                Volume: 0,
                Direction: dir,
                RunCount: runCount,
                ReversalPrice: revPrice,
                NextTargetPrice: nextExt,
                StartDate: blk.StartDate,
                EndDate: blk.EndDate,
                ReversalDate: trendSpan.ReversalDate,
                UpdateDate: trendSpan.UpdateDate,
                TrendDays: trendSpan.Days));
        }

        IReadOnlyList<MultiWaveSignal>? mwSignals = null;
        if (request.IncludeMultiWave && tlbSeries.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mw = new MultiWavePatternEngine();
            foreach (var b in tlbSeries)
            {
                mw.ProcessNextBlock(b);
            }
            mwSignals = mw.Signals.ToArray();
        }

        return new ChartSeriesCalculationResult(ChartType.ThreeLineBreak, candles.Count, elements.Count, null, null, elements, mwSignals, null);
    }
}
