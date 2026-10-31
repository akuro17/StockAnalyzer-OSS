using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_analyze_market_structure: identifies causal swing pivots, Break of Structure (BOS), and Change of Character (CHoCH)
/// events using the Core MarketStructureDetector in a strictly causal, prefix-invariant manner.
/// </summary>
internal sealed class AnalyzeMarketStructureTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string ZigZagThresholdArgument = "zigzagThresholdPercent";
    private const string LeftStrengthArgument = "leftStrength";
    private const string RightStrengthArgument = "rightStrength";
    private const string TailArgument = "outputTailCount";

    private const decimal DefaultZigZagThreshold = 5.0m;
    private const int DefaultStrength = IndicatorDefaultConstants.DivergencePivotLookback;

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;

    public AnalyzeMarketStructureTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string Name => "sa_analyze_market_structure";

    public string Description =>
        "Analyzes market structure swings and causal trend continuation/reversal shifts (BOS/CHoCH) " +
        "based on confirmed pivot points. Strictly causal and prefix-invariant: historical shifts never depend on future bars.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"zigzagThresholdPercent\":5.0,\"outputTailCount\":50}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(LookbackArgument, $"Stored bars loaded up to endDate, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Number(ZigZagThresholdArgument, $"Minimum price change percentage to confirm a swing pivot (0.1..50.0). Default {DefaultZigZagThreshold}%.")
        .Integer(LeftStrengthArgument, $"Fractal pivot strength to the left (>= 1). Default {DefaultStrength}.", defaultValue: DefaultStrength, minimum: 1)
        .Integer(RightStrengthArgument, $"Fractal pivot strength to the right (>= 1). Default {DefaultStrength}.", defaultValue: DefaultStrength, minimum: 1)
        .Integer(TailArgument, $"Maximum number of trailing pivots and shifts to return (1..{_limits.MaxOutputTailBars}). Default {_limits.DefaultOutputTailBars}.", defaultValue: _limits.DefaultOutputTailBars, minimum: 1, maximum: _limits.MaxOutputTailBars)
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
        int tail = ToolArgs.PositiveWithin(arguments, TailArgument, _limits.DefaultOutputTailBars, _limits.MaxOutputTailBars);

        double? rawThreshold = ToolArgs.Number(arguments, ZigZagThresholdArgument);
        if (rawThreshold.HasValue && (rawThreshold.Value < 0.1 || rawThreshold.Value > 50.0 || double.IsNaN(rawThreshold.Value) || double.IsInfinity(rawThreshold.Value)))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{ZigZagThresholdArgument} must be between 0.1 and 50.0.", ZigZagThresholdArgument);
        }
        decimal threshold = rawThreshold.HasValue ? (decimal)rawThreshold.Value : DefaultZigZagThreshold;

        int leftStrength;
        if (ToolArgs.Integer(arguments, LeftStrengthArgument) is { } rawLeft)
        {
            if (rawLeft < 1)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{LeftStrengthArgument} must be >= 1.", LeftStrengthArgument);
            }
            if (rawLeft > _limits.MaxCandlesPerRequest)
            {
                throw new ToolException(ToolErrorCodes.LimitExceeded, $"{LeftStrengthArgument} cannot exceed {_limits.MaxCandlesPerRequest}.", LeftStrengthArgument);
            }
            leftStrength = (int)rawLeft;
        }
        else
        {
            leftStrength = DefaultStrength;
        }

        int rightStrength;
        if (ToolArgs.Integer(arguments, RightStrengthArgument) is { } rawRight)
        {
            if (rawRight < 1)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{RightStrengthArgument} must be >= 1.", RightStrengthArgument);
            }
            if (rawRight > _limits.MaxCandlesPerRequest)
            {
                throw new ToolException(ToolErrorCodes.LimitExceeded, $"{RightStrengthArgument} cannot exceed {_limits.MaxCandlesPerRequest}.", RightStrengthArgument);
            }
            rightStrength = (int)rawRight;
        }
        else
        {
            rightStrength = DefaultStrength;
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

        // Strictly causal detection
        var confirmedPivots = MarketStructureDetector.ExtractConfirmedPivots(loaded, threshold);
        var shifts = MarketStructureDetector.DetectCausal(loaded, threshold);

        var latestShift = shifts.Count > 0 ? shifts[^1] : null;

        bool truncated = confirmedPivots.Count > tail || shifts.Count > tail;

        var returnedPivots = confirmedPivots.Count > tail
            ? confirmedPivots.Skip(confirmedPivots.Count - tail).ToList()
            : confirmedPivots;

        var returnedShifts = shifts.Count > tail
            ? shifts.Skip(shifts.Count - tail).ToList()
            : shifts;

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
            writer.WriteNumber("zigzagThresholdPercent", threshold);
            writer.WriteNumber("leftStrength", leftStrength);
            writer.WriteNumber("rightStrength", rightStrength);
            writer.WriteNumber("pivotCount", confirmedPivots.Count);
            writer.WriteNumber("shiftCount", shifts.Count);
            writer.WriteNumber("returnedPivotCount", returnedPivots.Count);
            writer.WriteNumber("returnedShiftCount", returnedShifts.Count);
            writer.WriteBoolean("truncated", truncated);

            writer.WriteStartArray("pivots");
            foreach (var p in returnedPivots)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", p.Index);
                writer.WriteString("date", p.Time.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
                writer.WriteNumber("price", p.Price);
                writer.WriteBoolean("isHigh", p.IsHigh);
                if (p.ConfirmedAtIndex.HasValue)
                {
                    writer.WriteNumber("confirmedAtIndex", p.ConfirmedAtIndex.Value);
                    if (p.ConfirmedAtIndex.Value >= 0 && p.ConfirmedAtIndex.Value < loaded.Count)
                    {
                        writer.WriteString("confirmedAtDate", loaded[p.ConfirmedAtIndex.Value].Timestamp.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("shifts");
            foreach (var s in returnedShifts)
            {
                bool isBullish = s.Type is MarketStructureType.BullishBOS or MarketStructureType.BullishCHoCH;
                decimal level = isBullish ? s.PreviousPivotHigh : s.PreviousPivotLow;
                DateTime brokenDate = isBullish ? s.PreviousPivotHighTime : s.PreviousPivotLowTime;

                writer.WriteStartObject();
                writer.WriteString("type", s.Type.ToString());
                writer.WriteString("direction", isBullish ? "Bullish" : "Bearish");
                writer.WriteNumber("level", level);
                writer.WriteString("brokenPivotDate", brokenDate != DateTime.MinValue ? brokenDate.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture) : "");
                writer.WriteString("date", s.Time.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
                writer.WriteNumber("index", s.Index);
                writer.WriteNumber("price", s.Price);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (latestShift is not null)
            {
                bool isBullish = latestShift.Type is MarketStructureType.BullishBOS or MarketStructureType.BullishCHoCH;
                decimal level = isBullish ? latestShift.PreviousPivotHigh : latestShift.PreviousPivotLow;
                DateTime brokenDate = isBullish ? latestShift.PreviousPivotHighTime : latestShift.PreviousPivotLowTime;

                writer.WriteStartObject("latestShift");
                writer.WriteString("type", latestShift.Type.ToString());
                writer.WriteString("direction", isBullish ? "Bullish" : "Bearish");
                writer.WriteNumber("level", level);
                writer.WriteString("brokenPivotDate", brokenDate != DateTime.MinValue ? brokenDate.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture) : "");
                writer.WriteString("date", latestShift.Time.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
                writer.WriteNumber("index", latestShift.Index);
                writer.WriteNumber("price", latestShift.Price);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("latestShift");
            }

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
