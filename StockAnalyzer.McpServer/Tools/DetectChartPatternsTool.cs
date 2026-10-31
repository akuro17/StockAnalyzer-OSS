using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ElliottWave;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Models.HarmonicPattern;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_detect_chart_patterns: detects macro chart patterns (Geometric formations, Harmonic patterns,
/// Elliott waves, and DTW Waveform matching) on historical candles, strictly filtered by confirmation date.
/// Strictly causal and prefix-invariant: historical patterns never depend on future bars.
/// </summary>
internal sealed class DetectChartPatternsTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string FamiliesArgument = "families";
    private const string MaxPatternsArgument = "maxPatterns";
    private const string GeometricZigZagThresholdArgument = "geometricZigZagThreshold";
    private const string HarmonicZigZagThresholdArgument = "harmonicZigZagThreshold";
    private const string ElliottZigZagThresholdArgument = "elliottZigZagThreshold";
    private const string DtwMinWindowArgument = "dtwMinWindow";
    private const string DtwMaxWindowArgument = "dtwMaxWindow";
    private const string DtwWindowStepArgument = "dtwWindowStep";
    private const string DtwThresholdArgument = "dtwThreshold";

    private static readonly string[] AllFamilies = { "Geometric", "Harmonic", "ElliottWave", "WaveformMatching" };

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;
    private readonly IPatternRecognitionService _dtwService;

    public DetectChartPatternsTool(
        IReadOnlyStockAnalyzerGateway gateway,
        SymbolResolver symbols,
        McpLimits limits,
        IPatternRecognitionService? dtwService = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _dtwService = dtwService ?? new PatternRecognitionService();
    }

    public string Name => "sa_detect_chart_patterns";

    public string Description =>
        "Detects macro chart patterns (Geometric formations, Harmonic patterns, Elliott waves, and DTW Waveform matching) " +
        "on historical candles, strictly filtered by confirmation date. Strictly causal and prefix-invariant: historical patterns never depend on future bars.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"families\":[\"Harmonic\",\"Geometric\"],\"maxPatterns\":50}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(LookbackArgument, $"Stored bars loaded up to endDate, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .StringArray(FamiliesArgument, "Subset of pattern families to detect (Geometric, Harmonic, ElliottWave, WaveformMatching). Default: all families.")
        .Integer(MaxPatternsArgument, $"Maximum number of patterns to return (1..{_limits.MaxReturnedPatterns}). Default {_limits.MaxReturnedPatterns}.", defaultValue: _limits.MaxReturnedPatterns, minimum: 1, maximum: _limits.MaxReturnedPatterns)
        .Number(GeometricZigZagThresholdArgument, $"Minimum price change percentage to confirm a geometric formation pivot (0.1..50.0). Default {ChartConstants.DefaultGeometricZigZagThreshold}%.")
        .Number(HarmonicZigZagThresholdArgument, $"Minimum leg price swing percentage for harmonic pattern detection (0.1..50.0). Default {ChartConstants.DefaultHarmonicMinLegSwingPercent}%.")
        .Number(ElliottZigZagThresholdArgument, $"Minimum price change percentage to confirm an Elliott wave pivot (0.1..50.0). Default {ChartConstants.DefaultElliottZigZagThreshold}%.")
        .Integer(DtwMinWindowArgument, $"Minimum window size in bars for DTW waveform matching (2..{_limits.MaxCandlesPerRequest}). Default {ChartConstants.PatternRecognitionDefaultMinWindow}.", defaultValue: ChartConstants.PatternRecognitionDefaultMinWindow, minimum: 2, maximum: _limits.MaxCandlesPerRequest)
        .Integer(DtwMaxWindowArgument, $"Maximum window size in bars for DTW waveform matching (minWindow..{_limits.MaxCandlesPerRequest}). Default {ChartConstants.PatternRecognitionDefaultMaxWindow}.", defaultValue: ChartConstants.PatternRecognitionDefaultMaxWindow, minimum: 2, maximum: _limits.MaxCandlesPerRequest)
        .Integer(DtwWindowStepArgument, $"Sliding window step size in bars for DTW waveform matching (1..{_limits.MaxCandlesPerRequest}). Default {ChartConstants.PatternRecognitionDefaultWindowStep}.", defaultValue: ChartConstants.PatternRecognitionDefaultWindowStep, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Number(DtwThresholdArgument, $"Minimum similarity threshold for DTW waveform matching (0.0..1.0). Default {ChartConstants.PatternRecognitionDefaultThreshold}.")
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept a symbol that is not in the ticker list but has stored bars. Default false.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        bool allowUnregistered = ToolArgs.Boolean(arguments, SymbolResolver.AllowUnregisteredArgument) ?? false;
        ResolvedSymbol resolved = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, allowUnregistered, cancellationToken).ConfigureAwait(false);
        string symbol = resolved.Symbol;
        IReadOnlyList<string> symbolWarnings = !resolved.IsRegistered
            ? new[] { SymbolResolver.OffListWarning(new[] { resolved.Symbol }) }
            : Array.Empty<string>();

        TimeFrame timeFrame = ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument);
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest);
        int maxPatterns = ToolArgs.PositiveWithin(arguments, MaxPatternsArgument, _limits.MaxReturnedPatterns, _limits.MaxReturnedPatterns);

        IReadOnlyList<string> requestedFamilies = ParseFamilies(arguments);

        decimal geoZigZag = ParseThreshold(arguments, GeometricZigZagThresholdArgument, ChartConstants.DefaultGeometricZigZagThreshold);
        decimal harmonicZigZag = ParseThreshold(arguments, HarmonicZigZagThresholdArgument, ChartConstants.DefaultHarmonicMinLegSwingPercent);
        decimal elliottZigZag = ParseThreshold(arguments, ElliottZigZagThresholdArgument, ChartConstants.DefaultElliottZigZagThreshold);

        int dtwMinWindow = ToolArgs.PositiveWithin(arguments, DtwMinWindowArgument, ChartConstants.PatternRecognitionDefaultMinWindow, _limits.MaxCandlesPerRequest);
        if (dtwMinWindow < 2)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{DtwMinWindowArgument} must be >= 2.", DtwMinWindowArgument);
        }

        int dtwMaxWindow = ToolArgs.PositiveWithin(arguments, DtwMaxWindowArgument, Math.Max(dtwMinWindow, ChartConstants.PatternRecognitionDefaultMaxWindow), _limits.MaxCandlesPerRequest);
        if (dtwMaxWindow < dtwMinWindow)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{DtwMaxWindowArgument} must be >= {DtwMinWindowArgument}.", DtwMaxWindowArgument);
        }

        int dtwWindowStep = ToolArgs.PositiveWithin(arguments, DtwWindowStepArgument, ChartConstants.PatternRecognitionDefaultWindowStep, _limits.MaxCandlesPerRequest);

        double dtwThreshold = ToolArgs.Number(arguments, DtwThresholdArgument) ?? ChartConstants.PatternRecognitionDefaultThreshold;
        if (dtwThreshold < 0.0 || dtwThreshold > 1.0 || double.IsNaN(dtwThreshold) || double.IsInfinity(dtwThreshold))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{DtwThresholdArgument} must be between 0.0 and 1.0.", DtwThresholdArgument);
        }

        List<CandleData> loaded;
        if (endDate is not null)
        {
            loaded = await _loader.LoadUpToAsync(symbol, timeFrame, endDate.Value, lookback, cancellationToken).ConfigureAwait(false);
            if (loaded.Count == 0)
            {
                var check = await _gateway.GetCandlesAsync(symbol, timeFrame, 1, cancellationToken).ConfigureAwait(false);
                if (check.Count == 0)
                {
                    throw new ToolException(ToolErrorCodes.SymbolNotFound, $"No stored market data exists for {symbol}.", SymbolArgument);
                }

                throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist on or before {endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture)} for {symbol}.", EndDateArgument);
            }
        }
        else
        {
            (loaded, _) = await _loader.LoadAsync(symbol, timeFrame, lookback, cancellationToken).ConfigureAwait(false);
            if (loaded.Count == 0)
            {
                throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist for {symbol}.", SymbolArgument);
            }
        }

        int evaluatedBars = loaded.Count;
        int excludedUnconfirmedCount = 0;
        var collected = new List<PatternItem>();

        foreach (string family in requestedFamilies)
        {
            switch (family)
            {
                case "Geometric":
                    CollectGeometric(loaded, evaluatedBars, geoZigZag, collected, ref excludedUnconfirmedCount);
                    break;
                case "Harmonic":
                    CollectHarmonic(loaded, evaluatedBars, harmonicZigZag, collected, ref excludedUnconfirmedCount);
                    break;
                case "ElliottWave":
                    CollectElliottWave(loaded, evaluatedBars, elliottZigZag, collected, ref excludedUnconfirmedCount);
                    break;
                case "WaveformMatching":
                    await CollectWaveformMatchingAsync(loaded, evaluatedBars, dtwMinWindow, dtwMaxWindow, dtwWindowStep, dtwThreshold, collected, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        var sorted = collected
            .OrderByDescending(p => p.EndDate, StringComparer.Ordinal)
            .ThenBy(p => p.Family, StringComparer.Ordinal)
            .ThenBy(p => p.StartDate, StringComparer.Ordinal)
            .ToList();

        bool truncated = sorted.Count > maxPatterns;
        IReadOnlyList<PatternItem> outputPatterns = truncated ? sorted.Take(maxPatterns).ToList() : sorted;

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", symbol);
            writer.WriteString("timeFrame", timeFrame.ToString());
            writer.WriteString("asOfDate", ToolJson.FormatDate(loaded[^1].Timestamp));
            writer.WriteNumber("evaluatedBars", evaluatedBars);
            writer.WriteBoolean("truncated", truncated);
            writer.WriteNumber("excludedUnconfirmedCount", excludedUnconfirmedCount);

            writer.WriteStartArray("patterns");
            foreach (PatternItem p in outputPatterns)
            {
                writer.WriteStartObject();
                writer.WriteString("family", p.Family);
                writer.WriteString("name", p.Name);
                writer.WriteString("direction", p.Direction);
                writer.WriteString("startDate", p.StartDate);
                writer.WriteString("endDate", p.EndDate);
                writer.WriteString("confirmedAtDate", p.ConfirmedAtDate);

                if (p.TargetPrice.HasValue)
                {
                    writer.WriteNumber("targetPrice", p.TargetPrice.Value);
                }
                if (p.Confidence.HasValue)
                {
                    writer.WriteNumber("confidence", p.Confidence.Value);
                }

                writer.WriteStartArray("points");
                foreach (PatternPointItem pt in p.Points)
                {
                    writer.WriteStartObject();
                    writer.WriteString("date", pt.Date);
                    writer.WriteNumber("price", pt.Price);
                    if (pt.Label is not null)
                    {
                        writer.WriteString("label", pt.Label);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (symbolWarnings.Count > 0)
            {
                writer.WriteStartArray("warnings");
                foreach (string w in symbolWarnings)
                {
                    writer.WriteStringValue(w);
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        });
    }

    private static IReadOnlyList<string> ParseFamilies(JsonElement arguments)
    {
        IReadOnlyList<string>? raw = ToolArgs.StringArray(arguments, FamiliesArgument);
        if (raw is null || raw.Count == 0)
        {
            return AllFamilies;
        }

        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(raw.Count);

        foreach (string item in raw)
        {
            string matched = AllFamilies.FirstOrDefault(f => string.Equals(f, item, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException(ToolErrorCodes.InvalidArgument, $"Unknown family '{item}'. Allowed families are: {string.Join(", ", AllFamilies)}.", FamiliesArgument);

            if (!distinct.Add(matched))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Duplicate family '{item}' in '{FamiliesArgument}' is not allowed.", FamiliesArgument);
            }

            result.Add(matched);
        }

        return result;
    }

    private static decimal ParseThreshold(JsonElement arguments, string name, decimal defaultValue)
    {
        double? raw = ToolArgs.Number(arguments, name);
        if (raw is null)
        {
            return defaultValue;
        }

        if (raw.Value < 0.1 || raw.Value > 50.0 || double.IsNaN(raw.Value) || double.IsInfinity(raw.Value))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{name} must be between 0.1 and 50.0.", name);
        }

        return (decimal)raw.Value;
    }

    private static void CollectHarmonic(
        IReadOnlyList<CandleData> loaded,
        int evaluatedBars,
        decimal threshold,
        List<PatternItem> collected,
        ref int excludedUnconfirmedCount)
    {
        IReadOnlyList<HarmonicPatternResult> results = HarmonicPatternDetector.DetectByLegAmplitude(loaded, threshold);

        foreach (HarmonicPatternResult h in results)
        {
            if (!h.ConfirmedAtIndex.HasValue || h.ConfirmedAtIndex.Value >= evaluatedBars)
            {
                excludedUnconfirmedCount++;
                continue;
            }

            if (h.D.Index >= evaluatedBars || h.X.Index >= evaluatedBars)
            {
                excludedUnconfirmedCount++;
                continue;
            }

            string confirmedAtDate = ToolJson.FormatDate(loaded[h.ConfirmedAtIndex.Value].Timestamp);
            string startDate = ToolJson.FormatDate(h.X.Time);
            string endDate = ToolJson.FormatDate(h.D.Time);
            string direction = h.IsBullish ? "Bullish" : "Bearish";

            var points = new List<PatternPointItem>
            {
                new(ToolJson.FormatDate(h.X.Time), h.X.Price, "X"),
                new(ToolJson.FormatDate(h.A.Time), h.A.Price, "A"),
                new(ToolJson.FormatDate(h.B.Time), h.B.Price, "B"),
                new(ToolJson.FormatDate(h.C.Time), h.C.Price, "C"),
                new(ToolJson.FormatDate(h.D.Time), h.D.Price, "D"),
            };

            collected.Add(new PatternItem(
                "Harmonic",
                h.PatternType.ToString(),
                direction,
                startDate,
                endDate,
                confirmedAtDate,
                null,
                h.ConfidenceScore,
                points));
        }
    }

    private static void CollectElliottWave(
        IReadOnlyList<CandleData> loaded,
        int evaluatedBars,
        decimal threshold,
        List<PatternItem> collected,
        ref int excludedUnconfirmedCount)
    {
        IReadOnlyList<ElliottWaveResult> results = ElliottWaveDetector.Detect(loaded, new[] { threshold });
        Dictionary<int, int> confirmedPivots = BuildConfirmedPivotMap(loaded, threshold);

        foreach (ElliottWaveResult w in results)
        {
            if (w.WavePoints.Count == 0)
            {
                continue;
            }

            PivotPoint terminal = w.WavePoints[^1];
            if (!confirmedPivots.TryGetValue(terminal.Index, out int confirmedIdx) || confirmedIdx >= evaluatedBars)
            {
                excludedUnconfirmedCount++;
                continue;
            }

            string confirmedAtDate = ToolJson.FormatDate(loaded[confirmedIdx].Timestamp);
            string startDate = ToolJson.FormatDate(w.WavePoints[0].Time);
            string endDate = ToolJson.FormatDate(terminal.Time);
            string direction = w.IsBullish ? "Bullish" : "Bearish";
            string name = w.IsImpulse ? "Impulse" : "Corrective";

            var points = new List<PatternPointItem>(w.WavePoints.Count);
            for (int i = 0; i < w.WavePoints.Count; i++)
            {
                string label = w.IsImpulse
                    ? i.ToString(CultureInfo.InvariantCulture)
                    : (i == 0 ? "0" : ((char)('A' + i - 1)).ToString(CultureInfo.InvariantCulture));

                points.Add(new PatternPointItem(
                    ToolJson.FormatDate(w.WavePoints[i].Time),
                    w.WavePoints[i].Price,
                    label));
            }

            collected.Add(new PatternItem(
                "ElliottWave",
                name,
                direction,
                startDate,
                endDate,
                confirmedAtDate,
                null,
                w.ConfidenceScore,
                points));
        }
    }

    private static void CollectGeometric(
        IReadOnlyList<CandleData> loaded,
        int evaluatedBars,
        decimal threshold,
        List<PatternItem> collected,
        ref int excludedUnconfirmedCount)
    {
        IReadOnlyList<DetectedFormation> formations = GeometricPatternDetector.Detect(loaded, threshold);
        Dictionary<int, int> confirmedPivots = BuildConfirmedPivotMap(loaded, threshold);

        foreach (DetectedFormation f in formations)
        {
            if (!confirmedPivots.TryGetValue(f.EndIndex, out int confirmedIdx) || confirmedIdx >= evaluatedBars)
            {
                excludedUnconfirmedCount++;
                continue;
            }

            string confirmedAtDate = ToolJson.FormatDate(loaded[confirmedIdx].Timestamp);
            string startDate = ToolJson.FormatDate(f.StartTime);
            string endDate = ToolJson.FormatDate(f.EndTime);

            string direction = f.Type switch
            {
                GeometricFormationType.BullishFlag => "Bullish",
                GeometricFormationType.AscendingChannel => "Bullish",
                GeometricFormationType.FallingWedge => "Bullish",
                GeometricFormationType.AscendingTriangle => "Bullish",
                GeometricFormationType.BearishFlag => "Bearish",
                GeometricFormationType.DescendingChannel => "Bearish",
                GeometricFormationType.RisingWedge => "Bearish",
                GeometricFormationType.DescendingTriangle => "Bearish",
                _ => "Neutral"
            };

            var points = new List<PatternPointItem>
            {
                new(ToolJson.FormatDate(f.StartTime), (decimal)f.UpperPriceAt(f.StartIndex), "UpperStart"),
                new(ToolJson.FormatDate(f.StartTime), (decimal)f.LowerPriceAt(f.StartIndex), "LowerStart"),
                new(ToolJson.FormatDate(f.EndTime), (decimal)f.UpperPriceAt(f.EndIndex), "UpperEnd"),
                new(ToolJson.FormatDate(f.EndTime), (decimal)f.LowerPriceAt(f.EndIndex), "LowerEnd"),
            };

            collected.Add(new PatternItem(
                "Geometric",
                f.Type.ToString(),
                direction,
                startDate,
                endDate,
                confirmedAtDate,
                null,
                f.ConfidenceScore,
                points));
        }
    }

    private static Dictionary<int, int> BuildConfirmedPivotMap(IReadOnlyList<CandleData> candles, decimal threshold)
    {
        var confirmedPivots = MarketStructureDetector.ExtractConfirmedPivots(candles, threshold);
        var map = new Dictionary<int, int>(confirmedPivots.Count);
        foreach (var p in confirmedPivots)
        {
            if (p.ConfirmedAtIndex.HasValue)
            {
                map[p.Index] = p.ConfirmedAtIndex.Value;
            }
        }
        return map;
    }

    private async Task CollectWaveformMatchingAsync(
        IReadOnlyList<CandleData> loaded,
        int evaluatedBars,
        int minWindow,
        int maxWindow,
        int windowStep,
        double threshold,
        List<PatternItem> collected,
        CancellationToken cancellationToken)
    {
        PatternRecognitionResult dtwResult = await _dtwService.DetectAsync(
            loaded,
            minWindow,
            maxWindow,
            windowStep,
            threshold,
            ChartConstants.DtwDefaultWarpingRadius,
            ChartConstants.DtwShortSpanPenaltyAlpha).ConfigureAwait(false);

        if (!dtwResult.IsSuccessful)
        {
            return;
        }

        foreach (DetectedPattern p in dtwResult.Patterns)
        {
            if (p.EndIndex >= evaluatedBars)
            {
                continue;
            }

            string confirmedAtDate = ToolJson.FormatDate(loaded[p.EndIndex].Timestamp);
            string startDate = ToolJson.FormatDate(loaded[p.StartIndex].Timestamp);
            string endDate = ToolJson.FormatDate(loaded[p.EndIndex].Timestamp);

            string direction = p.Name switch
            {
                "HeadAndShoulders" => "Bearish",
                "InverseHeadAndShoulders" => "Bullish",
                "DoubleTop" => "Bearish",
                "DoubleBottom" => "Bullish",
                "TripleTop" => "Bearish",
                "TripleBottom" => "Bullish",
                _ => "Neutral"
            };

            var points = new List<PatternPointItem>
            {
                new(startDate, loaded[p.StartIndex].Close, "Start"),
                new(endDate, loaded[p.EndIndex].Close, "End"),
            };

            collected.Add(new PatternItem(
                "WaveformMatching",
                p.Name,
                direction,
                startDate,
                endDate,
                confirmedAtDate,
                null,
                p.Probability,
                points));
        }
    }

    private sealed record PatternItem(
        string Family,
        string Name,
        string Direction,
        string StartDate,
        string EndDate,
        string ConfirmedAtDate,
        decimal? TargetPrice,
        double? Confidence,
        IReadOnlyList<PatternPointItem> Points);

    private sealed record PatternPointItem(
        string Date,
        decimal Price,
        string? Label);
}
