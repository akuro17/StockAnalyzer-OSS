using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_detect_candle_patterns: detects classical candlestick and price action patterns
/// (Doji, Engulfing, Hammer, Morning Star, Three Soldiers/Crows, Tasuki Gap, etc.) using CandlePatternDetector.
/// </summary>
internal sealed class DetectCandlePatternsTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string WindowBarsArgument = "windowBars";
    private const string ExcludePatternBarsArgument = "excludePatternBarsFromContext";
    private const string RequireTrendContextArgument = "requireTrendContext";

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;

    public DetectCandlePatternsTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string Name => "sa_detect_candle_patterns";

    public string Description =>
        "Detects classical single and multi-candle price action patterns (e.g. Doji, Hammer, Engulfing, Morning/Evening Star, " +
        "Three Soldiers/Crows, Tasuki Gap, Three-Line Strike, Rising/Falling Three Methods) using CandlePatternDetector. " +
        "Evaluates the latest bar or a trailing bar window. Strictly causal and read-only.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"windowBars\":5,\"requireTrendContext\":true}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(LookbackArgument, $"Stored bars loaded up to endDate, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(WindowBarsArgument, $"Number of trailing bars to scan for candlestick patterns (1..{_limits.MaxOutputTailBars}). Default 1 (evaluates latest bar only).", defaultValue: 1, minimum: 1, maximum: _limits.MaxOutputTailBars)
        .Boolean(ExcludePatternBarsArgument, "Exclude pattern constituent bars when computing local ATR and average body size volatility context. Default false.")
        .Boolean(RequireTrendContextArgument, "Require confirmed prior trend for reversal candlestick patterns to trigger. Default false.")
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
        int windowBars = ToolArgs.PositiveWithin(arguments, WindowBarsArgument, 1, _limits.MaxOutputTailBars);

        bool excludePatternBars = ToolArgs.Boolean(arguments, ExcludePatternBarsArgument) ?? false;
        bool requireTrend = ToolArgs.Boolean(arguments, RequireTrendContextArgument) ?? false;

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

        var patternMatches = new List<(int Index, DateTime Date, CandlePatternType Pattern)>();
        int startIdx = Math.Max(0, loaded.Count - windowBars);

        for (int i = startIdx; i < loaded.Count; i++)
        {
            var slice = loaded.GetRange(0, i + 1);
            var detected = CandlePatternDetector.DetectPatterns(slice, excludePatternBars, requireTrend);
            foreach (var p in detected)
            {
                if (p != CandlePatternType.None)
                {
                    patternMatches.Add((i, loaded[i].Timestamp, p));
                }
            }
        }

        var latestDetected = CandlePatternDetector.DetectPatterns(loaded, excludePatternBars, requireTrend)
            .Where(p => p != CandlePatternType.None)
            .Select(p => p.ToString())
            .ToList();

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", symbol);
            writer.WriteString("timeFrame", timeFrame.ToString());
            if (endDate is not null)
            {
                writer.WriteString("endDate", endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
            }
            writer.WriteNumber("lookbackBars", lookback);
            writer.WriteNumber("barCount", loaded.Count);
            writer.WriteNumber("windowBars", windowBars);

            writer.WriteStartArray("patterns");
            foreach (var m in patternMatches)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", m.Index);
                writer.WriteString("date", m.Date.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
                writer.WriteString("pattern", m.Pattern.ToString());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("latestPatterns");
            foreach (var pName in latestDetected)
            {
                writer.WriteStringValue(pName);
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
}
