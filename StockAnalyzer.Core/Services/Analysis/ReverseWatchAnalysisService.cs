using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;

namespace StockAnalyzer.Core.Services.Analysis;

public interface IReverseWatchAnalysisService
{
    /// <summary>
    /// Calculates Reverse Watch Curve points using an O(N) sliding window algorithm.
    /// Supports both Moving Average (smooth) and Raw Close-based modes, and optional Log10 scale for volume.
    /// </summary>
    ReverseWatchCurveData Calculate(IEnumerable<CandleData> candles, int period = 25, string stockCode = "", bool isMaBased = true, bool isLogScaleVolume = false);
}

public class ReverseWatchAnalysisService : IReverseWatchAnalysisService
{


    public ReverseWatchCurveData Calculate(IEnumerable<CandleData> candles, int period = 25, string stockCode = "", bool isMaBased = true, bool isLogScaleVolume = false)
    {
        if (period <= 0)
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");

        var candleList = candles.ToList();
        if (candleList.Count < period)
            throw new ArgumentException($"Insufficient data. Required: {period}, Available: {candleList.Count}");

        // Validate chronological order to prevent rendering artifacts
        for (int i = 1; i < candleList.Count; i++)
        {
            if (candleList[i].Timestamp.Date <= candleList[i - 1].Timestamp.Date)
                throw new ArgumentException("Candles must be chronologically ordered.");
        }

        var points = new List<ReverseWatchCurvePoint>(candleList.Count - period + 1);
        
        // Use decimal for accumulators to prevent precision loss (C001)
        decimal currentPriceSum = 0m;
        decimal currentVolumeSum = 0m;
        var windowSize = (decimal)period;

        // Initialize first window
        for (int i = 0; i < period; i++)
        {
            currentPriceSum += candleList[i].Close;
            currentVolumeSum += candleList[i].Volume;
        }

        decimal ApplyLog(decimal value) => isLogScaleVolume ? (decimal)Math.Log10(Math.Max(1.0, (double)value)) : value;

        points.Add(new ReverseWatchCurvePoint
        {
            Date = candleList[period - 1].Timestamp.Date,
            PriceAverage = isMaBased ? currentPriceSum / windowSize : candleList[period - 1].Close,
            VolumeAverage = ApplyLog(isMaBased ? currentVolumeSum / windowSize : candleList[period - 1].Volume),
            Open = candleList[period - 1].Open,
            High = candleList[period - 1].High,
            Low = candleList[period - 1].Low,
            Close = candleList[period - 1].Close,
            Volume = candleList[period - 1].Volume,
            Index = 0,
            Phase = ReverseWatchPhase.None
        });

        // Pass 1: Calculate Averages (O(N))
        for (int i = period; i < candleList.Count; i++)
        {
            var removed = candleList[i - period];
            var added = candleList[i];

            currentPriceSum = currentPriceSum - removed.Close + added.Close;
            currentVolumeSum = currentVolumeSum - removed.Volume + added.Volume;

            points.Add(new ReverseWatchCurvePoint
            {
                Date = added.Timestamp.Date,
                PriceAverage = isMaBased ? currentPriceSum / windowSize : added.Close,
                VolumeAverage = ApplyLog(isMaBased ? currentVolumeSum / windowSize : added.Volume),
                Open = added.Open,
                High = added.High,
                Low = added.Low,
                Close = added.Close,
                Volume = added.Volume,
                Index = i - period + 1,
                Phase = ReverseWatchPhase.None
            });
        }

        // Pass 2: Calculate Bounds (Global Min/Max) for the entire visible curve
        if (points.Count == 0) return new ReverseWatchCurveData { 
            Points = new(), 
            Bounds = new ReverseWatchCurveBounds { MinPrice = 0, MaxPrice = 0, MinVolume = 0, MaxVolume = 0 }, 
            Period = period, 
            StockCode = stockCode 
        };

        decimal minP = decimal.MaxValue, maxP = decimal.MinValue;
        decimal minV = decimal.MaxValue, maxV = decimal.MinValue;

        foreach (var p in points)
        {
            if (p.PriceAverage < minP) minP = p.PriceAverage;
            if (p.PriceAverage > maxP) maxP = p.PriceAverage;
            if (p.VolumeAverage < minV) minV = p.VolumeAverage;
            if (p.VolumeAverage > maxV) maxV = p.VolumeAverage;
        }

        // Ensure non-zero range
        if (minP == maxP) { minP *= ChartConstants.MinRangeShrinkFactor; maxP *= ChartConstants.MinRangeExpandFactor; }
        if (minV == maxV) { minV *= ChartConstants.MinRangeShrinkFactor; maxV *= ChartConstants.MinRangeExpandFactor; }
        
        // Add slight padding
        var pricePadding = (maxP - minP) * ChartConstants.BoundsPaddingPercent;
        var volPadding = (maxV - minV) * ChartConstants.BoundsPaddingPercent;
        var bounds = new ReverseWatchCurveBounds
        {
            MinPrice = minP - pricePadding,
            MaxPrice = maxP + pricePadding,
            MinVolume = Math.Max(0, minV - volPadding),
            MaxVolume = maxV + volPadding
        };

        // Pass 3: Assign Phases based on Position relative to Global Center (Midpoint of Bounds)
        // Center of the logical area (without padding, or with? Usually logic centers on Data Range)
        decimal centerP = (minP + maxP) / 2m;
        decimal centerV = (minV + maxV) / 2m;
        
        decimal rangeP = (maxP - minP) / 2m; // Radius equivalent for normalization
        decimal rangeV = (maxV - minV) / 2m;
        
        // Avoid div by zero
        if (rangeP == 0) rangeP = 1;
        if (rangeV == 0) rangeV = 1;

        foreach (var p in points)
        {
            // Deviations from Center
            decimal dPrice = p.PriceAverage - centerP;
            decimal dVol = p.VolumeAverage - centerV;

            // Normalize to -1..1 Unit Square
            double normPrice = (double)(dPrice / rangeP);
            double normVol = (double)(dVol / rangeV);

            // Calculate Angle
            // Y = normPrice, X = normVol
            double angle = Math.Atan2(normPrice, normVol);
            double degrees = angle * (180.0 / Math.PI);

            // Normalize to 0..360
            if (degrees < 0) degrees += 360;

            // Shift for 8-sector buckets centered on primary axes (0, 45, 90...)
            double shifted = degrees + 22.5;
            if (shifted >= 360) shifted -= 360; // Wrap around
            
            int sector = (int)(shifted / 45.0);

            // Mapping per User Image/Table:
            // Sector 0 (East) -> Phase 3
            // Sector 1 (NE)   -> Phase 4
            // Sector 2 (North)-> Phase 5
            // Sector 3 (NW)   -> Phase 6
            // Sector 4 (West) -> Phase 7
            // Sector 5 (SW)   -> Phase 8
            // Sector 6 (South)-> Phase 1
            // Sector 7 (SE)   -> Phase 2

            p.Phase = sector switch
            {
                0 => ReverseWatchPhase.Phase2, // East: Buy Continuation (買い乗せ)
                1 => ReverseWatchPhase.Phase3, // NE: Caution (天井警戒)
                2 => ReverseWatchPhase.Phase4, // North: Bearish Reversal (陰転)
                3 => ReverseWatchPhase.Phase5, // NW: Sell Signal (売り)
                4 => ReverseWatchPhase.Phase6, // West: Sell Continuation (売り乗せ)
                5 => ReverseWatchPhase.Phase7, // SW: Bottoming Out (底入れ)
                6 => ReverseWatchPhase.Phase8, // South: Bullish Reversal (陽転)
                7 => ReverseWatchPhase.Phase1, // SE: Buy Signal (買い)
                _ => ReverseWatchPhase.None
            };
        }
        
        // Scaling Factor: Range Ratio
        double scale = (double)(rangeP / rangeV);

        return new ReverseWatchCurveData
        {
            Points = points,
            Period = period,
            StockCode = stockCode,
            Bounds = bounds,
            ScalingFactor = scale
        };
    }
}
