using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.Core.Models.Confluence;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;

namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// Thread-safe data snapshot for chart rendering.
/// Immutable DTO for safely transferring data from UI thread to rendering thread.
/// </summary>
/// <remarks>
/// ICustomDrawOperation may run on a different thread,
/// so direct references to ObservableCollection can cause race conditions.
/// This snapshot pattern guarantees data consistency at the time of rendering.
/// </remarks>
public sealed class ChartDataSnapshot
{
    /// <summary>
    /// Read-only list of candle data.
    /// </summary>
    public IReadOnlyList<CoreCandleData> Candles { get; }
    
    /// <summary>
    /// For special chart types: Full pre-converted candle list (unsliced).
    /// Used for referencing anchor points off-screen.
    /// </summary>
    public IReadOnlyList<CoreCandleData>? AllCandles { get; }
    
    /// <summary>
    /// For Kagi charts: Pre-calculated mapping from segment index to column index.
    /// Eliminates O(N) loops in the renderer.
    /// </summary>
    public IReadOnlyList<int>? KagiColumnMap { get; }

    /// <summary>
    /// Maximum price within the display range.
    /// </summary>
    public decimal MaxPrice { get; }

    /// <summary>
    /// Minimum price within the display range.
    /// </summary>
    public decimal MinPrice { get; }

    /// <summary>
    /// Price range (MaxPrice - MinPrice). Guaranteed minimum of 1 to prevent division by zero.
    /// </summary>
    public decimal PriceRange { get; }

    /// <summary>
    /// Maximum volume within the display range. Guaranteed minimum of 1 to prevent division by zero.
    /// </summary>
    public long MaxVolume { get; }

    /// <summary>
    /// Display start index.
    /// </summary>
    public int StartIndex { get; }

    /// <summary>
    /// Display end index.
    /// </summary>
    public int EndIndex { get; }

    /// <summary>
    /// Symbol name.
    /// </summary>
    public string Symbol { get; } = string.Empty;

    /// <summary>
    /// For Renko/P&F charts: Minimum expected brick/box size in price units.
    /// Used for precise horizontal/vertical alignment.
    /// </summary>
    public decimal MinBrickSize { get; }
    
    /// <summary>
    /// For Renko/P&F charts: Maximum expected brick/box size in price units.
    /// Used for precise horizontal/vertical alignment.
    /// </summary>
    public decimal MaxBrickSize { get; }

    /// <summary>
    /// Timeframe.
    /// </summary>
    public string Timeframe { get; } = string.Empty;

    /// <summary>
    /// The requested visible candle count (viewport width in bars).
    /// Used for rendering calculations to determine bar width, even if actual candles are fewer.
    /// </summary>
    public int VisibleCandleCount { get; }

    /// <summary>
    /// Indicator results aligned with Candles list.
    /// Key: Indicator ID/Name.
    /// </summary>
    public IReadOnlyDictionary<string, IIndicatorResult>? IndicatorResults { get; }

    /// <summary>
    /// Legacy accessor for backward compatibility with simple line renderers.
    /// Returns the main series of the indicator.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<decimal?>>? IndicatorValues { get; }

    /// <summary>
    /// Settings for displaying legends (Color, Name).
    /// </summary>
    public IReadOnlyList<CoreIndicatorSettings> IndicatorSettings { get; }
    
    /// <summary>
    /// Drawings to render.
    /// </summary>
    public IReadOnlyList<DrawingItem> Drawings { get; }

    /// <summary>
    /// Pre-calculated P&F analysis result (signals and trendlines).
    /// </summary>
    public PnfAnalysisResult? PnfAnalysis { get; }

    /// <summary>
    /// Pre-calculated Multi-Wave engine signals.
    /// </summary>
    public IReadOnlyList<MultiWaveSignal>? MultiWaveSignals { get; }

    /// <summary>
    /// For Three Line Break: Current trend count (number of same-colored bars in the latest sequence).
    /// </summary>
    public int? ThreeLineBreakCount { get; }

    /// <summary>
    /// The chart type (e.g., Candlestick, PointAndFigure, HeikinAshi, etc.).
    /// </summary>
    public ChartType ChartType { get; }

    /// <summary>
    /// Combined signal confluence result.
    /// </summary>
    public ConfluenceResult? Confluence { get; }
    
    /// <summary>
    /// Additional metadata for rendering (e.g., pre-formatted labels for ZeroAllocation).
    /// </summary>
    public IReadOnlyDictionary<string, string> RenderingMetadata { get; }
    
    /// <summary>
    /// Vertical padding at the top of the chart (pixels).
    /// </summary>
    public decimal PaddingTopPx { get; }

    /// <summary>
    /// Vertical padding at the bottom of the chart (pixels).
    /// </summary>
    public decimal PaddingBottomPx { get; }

    /// <summary>
    /// Total chart area height (pixels).
    /// </summary>
    public decimal ChartHeightPx { get; }

    /// <summary>
    /// Pre-calculated relative values for comparison charts (Performance %, Ratio, or Z-Score).
    /// Key: Symbol name. Value: Current display range values.
    /// </summary>
    public IReadOnlyDictionary<string, decimal?[]>? ComparisonSeries { get; }


    /// <summary>
    /// Creates a new snapshot.
    /// </summary>
    /// <param name="candles">Original candle data. Copied internally.</param>
    /// <param name="symbol">Symbol name.</param>
    /// <param name="timeframe">Timeframe name.</param>
    /// <param name="indicatorValues">Pre-calculated indicator values.</param>
    /// <param name="indicatorSettings">Indicator settings for legend.</param>
    /// <param name="startIndex">Display start index (0-based index of the original collection).</param>
    /// <param name="count">Number of candles to display. If -1, displays from startIndex to the end.</param>
    public ChartDataSnapshot(
        IEnumerable<Candle> candles, 
        string symbol = "", 
        string timeframe = "", 
        IReadOnlyDictionary<string, IIndicatorResult>? indicatorResults = null,
        IEnumerable<CoreIndicatorSettings>? indicatorSettings = null,
        IEnumerable<DrawingItem>? drawings = null,
        int startIndex = 0, 
        int count = -1,
        decimal paddingTopPx = 0,
        decimal paddingBottomPx = 0,
        decimal chartHeightPx = 0,
        decimal minBrickSize = 0,
        decimal maxBrickSize = 0,
        PnfAnalysisResult? pnfAnalysis = null,
        IReadOnlyList<MultiWaveSignal>? multiWaveSignals = null,
        IReadOnlyList<CoreCandleData>? allPnfCandles = null,
        ChartType chartType = ChartType.Candlestick,
        int? threeLineBreakCount = null,
        IPriceRangeCalculator? priceRangeCalculator = null,
        ConfluenceResult? confluence = null,
        IReadOnlyDictionary<string, string>? renderingMetadata = null,
        IReadOnlyDictionary<string, decimal?[]>? comparisonSeries = null,
        decimal? overrideMinPrice = null,
        decimal? overrideMaxPrice = null,
        int visibleCandleCount = 100,
        IReadOnlyList<int>? kagiColumnMap = null)
        : this(candles.Select(c => new CoreCandleData(c.Date, c.Open, c.High, c.Low, c.Close, c.Volume)),
               symbol, timeframe, indicatorResults, indicatorSettings, drawings, startIndex, count,
               paddingTopPx, paddingBottomPx, chartHeightPx, minBrickSize, maxBrickSize, pnfAnalysis,
               multiWaveSignals, allPnfCandles, chartType, threeLineBreakCount, priceRangeCalculator,
               confluence, renderingMetadata, comparisonSeries, overrideMinPrice, overrideMaxPrice,
               visibleCandleCount, kagiColumnMap)
    {
    }

    public ChartDataSnapshot(
        IEnumerable<CoreCandleData> candles, 
        string symbol = "", 
        string timeframe = "", 
        IReadOnlyDictionary<string, IIndicatorResult>? indicatorResults = null,
        IEnumerable<CoreIndicatorSettings>? indicatorSettings = null,
        IEnumerable<DrawingItem>? drawings = null,
        int startIndex = 0, 
        int count = -1,
        decimal paddingTopPx = 0,
        decimal paddingBottomPx = 0,
        decimal chartHeightPx = 0,
        decimal minBrickSize = 0,
        decimal maxBrickSize = 0,
        PnfAnalysisResult? pnfAnalysis = null,
        IReadOnlyList<MultiWaveSignal>? multiWaveSignals = null,
        IReadOnlyList<CoreCandleData>? allPnfCandles = null,
        ChartType chartType = ChartType.Candlestick,
        int? threeLineBreakCount = null,
        IPriceRangeCalculator? priceRangeCalculator = null,
        ConfluenceResult? confluence = null,
        IReadOnlyDictionary<string, string>? renderingMetadata = null,
        IReadOnlyDictionary<string, decimal?[]>? comparisonSeries = null,
        decimal? overrideMinPrice = null,
        decimal? overrideMaxPrice = null,
        int visibleCandleCount = 100,
        IReadOnlyList<int>? kagiColumnMap = null)
    {
        VisibleCandleCount = visibleCandleCount;
        KagiColumnMap = kagiColumnMap;
        var priceCalc = priceRangeCalculator ?? StandardPriceRangeCalculator.Instance;
        ChartType = chartType;
        PaddingTopPx = paddingTopPx;
        PaddingBottomPx = paddingBottomPx;
        ChartHeightPx = chartHeightPx;
        MinBrickSize = minBrickSize;
        MaxBrickSize = maxBrickSize;
        PnfAnalysis = pnfAnalysis;
        MultiWaveSignals = multiWaveSignals;
        Confluence = confluence;
        RenderingMetadata = renderingMetadata ?? new Dictionary<string, string>();
        ComparisonSeries = comparisonSeries;
        AllCandles = allPnfCandles != null ? new List<CoreCandleData>(allPnfCandles) : null;
        ThreeLineBreakCount = threeLineBreakCount;
        var allCandles = candles as List<CoreCandleData> ?? candles.ToList();
        int totalCount = allCandles.Count;

        // Validate range
        if (startIndex < 0) startIndex = 0;
        // RELAXED CLAMPING (Phase 4): Allow scrolling past totalCount for future projection.
        // We still need a reasonable upper bound to prevent overflow, but it should be based on Total + FutureOffset.
        // Since we don't know FutureOffset here easily without passing it, and the Coordinator manages limits,
        // we trust the incoming startIndex up to a point, or clamp to a very large number?
        // Actually, Coordinator clamps it to (Total + Offset).
        // Let's just clamp to ensure it's not negative.
        // if (startIndex >= totalCount) startIndex = Math.Max(0, totalCount - 1); // REMOVED
        
        int availableCount = Math.Max(0, totalCount - startIndex);
        int displayCount = (count < 0) ? availableCount : count; // Uses requested count potentially

        // Slice Candles
        List<CoreCandleData> slicedCandles;
        if (startIndex >= totalCount)
        {
            slicedCandles = new List<CoreCandleData>();
        }
        else
        {
            // Clamp count to available for ACTUAL data slicing
            int actualSliceCount = Math.Min(displayCount, availableCount);
            slicedCandles = allCandles.GetRange(startIndex, actualSliceCount);
        }
        
        // Use sliced data
        decimal maxPrice = decimal.MinValue;
        decimal minPrice = decimal.MaxValue;
        long maxVolume = 0;

        foreach (var c in slicedCandles)
        {
            var (cHigh, cLow) = priceCalc.GetPriceRange(c);

            if (cHigh > maxPrice) maxPrice = cHigh;
            if (cLow < minPrice) minPrice = cLow;
            if (c.Volume > maxVolume) maxVolume = c.Volume;
        }

        Candles = slicedCandles;
        StartIndex = startIndex;
        
        int actualCount = slicedCandles.Count;
        int validEndIndex = startIndex + actualCount - 1;
        if (validEndIndex < startIndex) validEndIndex = startIndex - 1;
        EndIndex = validEndIndex;
        
        // New Property to track the requested view width (including future empty space)
        VisibleCandleCount = count > 0 ? count : displayCount;

        Symbol = symbol;
        Timeframe = timeframe;
        MinBrickSize = minBrickSize;
        MaxBrickSize = maxBrickSize;
        
        // Slice and Convert Indicators
        Dictionary<string, IIndicatorResult>? indResults = null;
        Dictionary<string, IReadOnlyList<decimal?>>? indValues = null;
        
        // For indicators, we want to allow slicing beyond the candle count IF the indicator has data (Future data).
        // The displayCount calculated above is clamped to availableCandles.
        // But for indicators, we should use the requested 'count' (VisibleCandleCount) if provided, 
        // clamped only to the indicator's own total count.
        
        if (indicatorResults != null)
        {
            indResults = new Dictionary<string, IIndicatorResult>();
            indValues = new Dictionary<string, IReadOnlyList<decimal?>>();

            var settingsMap = indicatorSettings?.ToDictionary(s => s.Id) ?? new Dictionary<string, CoreIndicatorSettings>();

            foreach(var kvp in indicatorResults)
            {
                // We pass the requested 'count' (VisibleCandleCount) to SlicedIndicatorResult.
                // SlicedIndicatorResult Logic:
                // It takes (original, startIndex, count, totalCount).
                // It slices original.MainValues.
                // If original has 1026 values (1000 candles + 26 future), and startIndex is 900, count is 100.
                // It returns 100 values.
                // If startIndex is 950, count is 100.
                // 950 + 100 = 1050 > 1026.
                // It should return 76 values (up to 1026).
                // The renderer will then draw 76 values.
                
                // If we pass 'displayCount' (which is clamped to candles), we lose future data.
                // So we must pass 'count' (requested visible).
                
                int targetCount = (count > 0) ? count : displayCount;
                
                var slicedResult = new SlicedIndicatorResult(kvp.Value, startIndex, targetCount, totalCount);
                indResults[kvp.Key] = slicedResult;
                indValues[kvp.Key] = slicedResult.MainValues;

                // Update Min/Max for Overlays
                if (settingsMap.TryGetValue(kvp.Key, out var setting) && setting.IsOverlay && setting.IsEnabled)
                {
                    foreach(var seriesName in slicedResult.SeriesNames)
                    {
                         // Skip non-price series from scaling logic
                         if (seriesName == "IsUpTrend") continue;
                         if (setting.TypeEnum == IndicatorType.GranvilleLaw && (seriesName == "BuySignals" || seriesName == "SellSignals")) continue;

                         var values = slicedResult.GetSeries(seriesName);
                         if (values == null) continue;
                         
                         // WebAI Fix: Only consider indices that have actual candles to prevent scaling bugs
                         // from future/padding data in indicators.
                         int limit = Math.Min(values.Count, Candles.Count);
                         for (int i = 0; i < limit; i++)
                         {
                             var v = values[i];
                             if (v.HasValue)
                             {
                                 if (v.Value > maxPrice) maxPrice = v.Value;
                                 if (v.Value < minPrice) minPrice = v.Value;
                             }
                         }
                    }
                }
            }
        }
        IndicatorResults = indResults;
        IndicatorValues = indValues; // Backward compatibility

        IndicatorSettings = indicatorSettings?.ToList().AsReadOnly() ?? new List<CoreIndicatorSettings>().AsReadOnly();
        Drawings = drawings?.ToList().AsReadOnly() ?? new List<DrawingItem>().AsReadOnly();

        // Calculate Scale
        if (overrideMinPrice.HasValue && overrideMaxPrice.HasValue)
        {
            MinPrice = overrideMinPrice.Value;
            MaxPrice = overrideMaxPrice.Value;
            
            // Standard padding still applies to the forced range to clear the header/axis
            if (chartHeightPx > 0 && (paddingTopPx > 0 || paddingBottomPx > 0))
            {
                decimal currentRange = MaxPrice - MinPrice;
                if (currentRange == 0) currentRange = 1m;
                decimal pricePerPixel = currentRange / Math.Max(1m, chartHeightPx);
                
                MaxPrice += paddingTopPx * pricePerPixel;
                MinPrice -= paddingBottomPx * pricePerPixel;
            }
        }
        else if (Candles.Count > 0)
        {
            // Ensure min < max
             if (minPrice > maxPrice) 
             {
                  MaxPrice = 100m;
                  MinPrice = 0m;
             }
             else
             {
                 var currentRange = maxPrice - minPrice;
                 if (currentRange == 0) currentRange = 1m;
                 
                 // Apply TradingView-style price padding based on physical pixels
                 if (chartHeightPx > 0)
                 {
                     decimal pricePerPixel = currentRange / Math.Max(1m, chartHeightPx);
                     maxPrice += paddingTopPx * pricePerPixel;
                     minPrice -= paddingBottomPx * pricePerPixel;
                 }
                 
                 MaxPrice = maxPrice;
                 MinPrice = minPrice;
             }
        }
        else
        {
            // Default range if no data
            MaxPrice = 100m;
            MinPrice = 0m;
        }

        var range = MaxPrice - MinPrice;
        PriceRange = range == 0 ? 1m : range;
        MaxVolume = maxVolume == 0 ? 1L : maxVolume;
    }

    /// <summary>
    /// Maps a raw candle/segment index to a logical X coordinate based on the chart type.
    /// Handles Kagi column mapping (O(1)) and Renko/TLB centering.
    /// </summary>
    public double GetLogicalXIndex(int targetIndex, IChartRenderConfig config)
    {
        if (config.ChartType == ChartType.Kagi)
        {
            if (KagiColumnMap != null)
            {
                int safeIdx = Math.Clamp(targetIndex, 0, KagiColumnMap.Count - 1);
                return (double)KagiColumnMap[safeIdx];
            }

            // Fallback for Kagi if map is missing (Legacy loop - should be rare)
            int columnIdx = 0;
            int limit = Math.Min(targetIndex, Candles.Count - 1);
            for (int i = 1; i <= limit; i++)
            {
                // Detect flat anchor at index 0 (Open == Close): always advance column at index 1
                bool isAnchorTransition = (i == 1 && Candles[0].Open == Candles[0].Close);
                bool isDirectionChange = (Candles[i].Close >= Candles[i].Open) != (Candles[i-1].Close >= Candles[i-1].Open);
                if (isDirectionChange || isAnchorTransition)
                {
                    columnIdx++;
                }
            }
            int initialCol = config is IKagiRenderConfig kagi ? kagi.KagiInitialColumn : 0;
            return (double)initialCol + columnIdx;
        }

        return (double)StartIndex + targetIndex;
    }

    /// <summary>
    /// Creates a new snapshot from pre-converted CoreCandleData.
    /// Used for Heikin Ashi or other pre-calculated candle types.
    /// </summary>
    public ChartDataSnapshot(
        IReadOnlyList<CoreCandleData> coreCandles,
        string symbol = "",
        string timeframe = "",
        IReadOnlyDictionary<string, IIndicatorResult>? indicatorResults = null,
        IEnumerable<CoreIndicatorSettings>? indicatorSettings = null,
        IEnumerable<DrawingItem>? drawings = null,
        int startIndex = 0,
        int count = -1,
        decimal paddingTopPx = 0,
        decimal paddingBottomPx = 0,
        decimal chartHeightPx = 0,
        decimal minBrickSize = 0,
        decimal maxBrickSize = 0,
        PnfAnalysisResult? pnfAnalysis = null,
        IReadOnlyList<MultiWaveSignal>? multiWaveSignals = null,
        IReadOnlyList<CoreCandleData>? allCandles = null,
        ChartType chartType = ChartType.Candlestick,
        int? threeLineBreakCount = null,
        IPriceRangeCalculator? priceRangeCalculator = null,
        ConfluenceResult? confluence = null,
        IReadOnlyDictionary<string, string>? renderingMetadata = null,
        IReadOnlyDictionary<string, decimal?[]>? comparisonSeries = null,
        decimal? overrideMinPrice = null,
        decimal? overrideMaxPrice = null)
    {
        var priceCalc = priceRangeCalculator ?? StandardPriceRangeCalculator.Instance;
        ChartType = chartType;
        PaddingTopPx = paddingTopPx;
        PaddingBottomPx = paddingBottomPx;
        ChartHeightPx = chartHeightPx;
        MinBrickSize = minBrickSize;
        MaxBrickSize = maxBrickSize;
        PnfAnalysis = pnfAnalysis;
        MultiWaveSignals = multiWaveSignals;
        Confluence = confluence;
        RenderingMetadata = renderingMetadata ?? new Dictionary<string, string>();
        ComparisonSeries = comparisonSeries;
        AllCandles = allCandles != null ? new List<CoreCandleData>(allCandles) : null;
        ThreeLineBreakCount = threeLineBreakCount;
        int totalCount = coreCandles.Count;
        
        // Validate range
        if (startIndex < 0) startIndex = 0;
        // RELAXED CLAMPING (Phase 4)
        // if (startIndex >= totalCount) startIndex = Math.Max(0, totalCount - 1); // REMOVED
        
        int availableCount = Math.Max(0, totalCount - startIndex);
        int displayCount = (count < 0) ? availableCount : count;

        // Slice Candles (CoreCandleData is struct, lightweight)
        // We need to create a new list for the slice or just use Linq (but Linq is slower for indexed access during render)
        // Since we are creating a snapshot, let's materialize the slice.
        
        // Optimization: simple array copy or loop
        // Handle empty slice
        int actualSliceCount = Math.Min(displayCount, availableCount);
        if (actualSliceCount < 0) actualSliceCount = 0; // Safety
        
        var slicedCandles = new List<CoreCandleData>(actualSliceCount);
        decimal maxPrice = decimal.MinValue;
        decimal minPrice = decimal.MaxValue;
        long maxVolume = 0;

        for (int i = 0; i < actualSliceCount; i++)
        {
            var c = coreCandles[startIndex + i];
            slicedCandles.Add(c);
            
            var (cHigh, cLow) = priceCalc.GetPriceRange(c);

            if (cHigh > maxPrice) maxPrice = cHigh;
            if (cLow < minPrice) minPrice = cLow;
            if (c.Volume > maxVolume) maxVolume = c.Volume;
        }

        Candles = slicedCandles.ToImmutableArray();
        StartIndex = startIndex;
        // EndIndex logic: depends on what "EndIndex" means. 
        // If it means "Index of last displayed data point", it should be startIndex + actualSliceCount - 1.
        // If it means "Index of rightmost slot", it might be different.
        // Standard usage seems to be for iteration.
        int validEndIndex = startIndex + actualSliceCount - 1;
        if (validEndIndex < startIndex) validEndIndex = startIndex - 1; // Empty
        EndIndex = validEndIndex;
        
        // New Property to track the requested view width (including future empty space)
        VisibleCandleCount = count > 0 ? count : displayCount;

        Symbol = symbol;
        Timeframe = timeframe;
        MinBrickSize = minBrickSize;
        MaxBrickSize = maxBrickSize;

        // Slice and Convert Indicators to ReadOnly
        var indResults = new Dictionary<string, IIndicatorResult>();
        var indValues = new Dictionary<string, IReadOnlyList<decimal?>>();

        if (indicatorResults != null)
        {
            var settingsMap = indicatorSettings?.ToDictionary(s => s.Id) ?? new Dictionary<string, CoreIndicatorSettings>();

            foreach (var kvp in indicatorResults)
            {
                // Fix for Heikin Ashi path: Use VisibleCandleCount
                int targetCount = (count > 0) ? count : displayCount;

                var slicedResult = new SlicedIndicatorResult(kvp.Value, startIndex, targetCount, totalCount);
                indResults[kvp.Key] = slicedResult;
                indValues[kvp.Key] = slicedResult.MainValues;

                 // Update Min/Max for Overlays
                if (settingsMap.TryGetValue(kvp.Key, out var setting) && setting.IsOverlay && setting.IsEnabled)
                {
                    foreach(var seriesName in slicedResult.SeriesNames)
                    {
                         // Skip non-price series from scaling logic
                         if (seriesName == "IsUpTrend") continue;
                         if (setting.TypeEnum == IndicatorType.GranvilleLaw && (seriesName == "BuySignals" || seriesName == "SellSignals")) continue;

                         var values = slicedResult.GetSeries(seriesName);
                         if (values == null) continue;
                         
                         // WebAI Fix: Only consider indices that have actual candles to prevent scaling bugs
                         // from future/padding data in indicators.
                         int limit = Math.Min(values.Count, actualSliceCount);
                         for (int i = 0; i < limit; i++)
                         {
                             var v = values[i];
                             if (v.HasValue)
                             {
                                 if (v.Value > maxPrice) maxPrice = v.Value;
                                 if (v.Value < minPrice) minPrice = v.Value;
                             }
                         }
                    }
                }
            }
        }
        IndicatorResults = indResults;
        IndicatorValues = indValues;

        IndicatorSettings = indicatorSettings?.ToList().AsReadOnly() ?? new List<CoreIndicatorSettings>().AsReadOnly();
        Drawings = drawings?.ToList().AsReadOnly() ?? new List<DrawingItem>().AsReadOnly();

        // Phase 4: Include Ghost Projections (MultiWave Targets) in Price Range
        if (multiWaveSignals != null)
        {
            foreach (var signal in multiWaveSignals)
            {
                if (signal.IsInvalidated) continue;
                
                // Only consider signals that are visible in the current viewport
                if (signal.TriggerIndex >= startIndex - 5 && signal.TriggerIndex < startIndex + count + 5)
                {
                    if (signal.TargetPriceMax > maxPrice) maxPrice = signal.TargetPriceMax;
                    if (signal.TargetPriceMin < minPrice && signal.TargetPriceMin > 0) minPrice = signal.TargetPriceMin;
                }
            }
        }

        // Calculate Scale
        if (overrideMinPrice.HasValue && overrideMaxPrice.HasValue)
        {
            MinPrice = overrideMinPrice.Value;
            MaxPrice = overrideMaxPrice.Value;
            
            if (chartHeightPx > 0 && (paddingTopPx > 0 || paddingBottomPx > 0))
            {
                decimal currentRange = MaxPrice - MinPrice;
                if (currentRange == 0) currentRange = 1m;
                decimal pricePerPixel = currentRange / Math.Max(1m, chartHeightPx);
                
                MaxPrice += paddingTopPx * pricePerPixel;
                MinPrice -= paddingBottomPx * pricePerPixel;
            }
        }
        else if (slicedCandles.Count > 0)
        {
             if (minPrice > maxPrice) 
             {
                  MaxPrice = 100m;
                  MinPrice = 0m;
             }
             else
             {
                 var currentRange = maxPrice - minPrice;
                 if (currentRange == 0) currentRange = 1m;
                 
                 // Apply TradingView-style price padding based on physical pixels
                 if (chartHeightPx > 0)
                 {
                     decimal pricePerPixel = currentRange / Math.Max(1m, chartHeightPx);
                     maxPrice += paddingTopPx * pricePerPixel;
                     minPrice -= paddingBottomPx * pricePerPixel;
                 }
                 
                 MaxPrice = maxPrice;
                 MinPrice = minPrice;
             }
        }
        else
        {
            MaxPrice = 100m;
            MinPrice = 0m;
        }

        var range = MaxPrice - MinPrice;
        PriceRange = range == 0 ? 1m : range;
        MaxVolume = maxVolume == 0 ? 1L : maxVolume;
    }

    /// <summary>
    /// Creates an empty snapshot.
    /// </summary>
    public static ChartDataSnapshot Empty => new(Array.Empty<Candle>());
}
