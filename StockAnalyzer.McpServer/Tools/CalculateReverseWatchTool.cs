using System.Text.Json;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_calculate_reverse_watch: the Reverse Watch curve the chart draws, for the latest bars of one symbol and time frame. The curve and its
/// phases come from the application's calculation (the gateway's CalculateReverseWatch); the tool adds only the window-position labels of
/// <see cref="ReverseWatchPhaseGeometry"/> and the argument mapping.
/// </summary>
internal sealed class CalculateReverseWatchTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string EndDateArgument = "endDate";
    private const string PeriodArgument = "period";
    private const string MaBasedArgument = "isMaBased";
    private const string LogVolumeArgument = "isLogScaleVolume";
    private const string DataCountArgument = "dataCount";
    private const string LookbackArgument = "lookbackBars";
    private const string TailArgument = "outputTailCount";

    private const bool DefaultMaBased = true;
    private const bool DefaultLogScaleVolume = false;

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;

    public CalculateReverseWatchTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway;
        _symbols = symbols;
        _loader = new CandleWindowLoader(gateway);
        _limits = limits;
    }

    public string Name => "sa_calculate_reverse_watch";

    public string Description =>
        "Calculates the Reverse Watch curve the chart draws, for the latest bars of one symbol and time frame. Each point carries its phase (Phase1 to Phase8, the eight 45-degree sectors of the price-volume plane) " +
        "and where it lies in the window of the last dataCount points: pricePosition and volumePosition are High, Middle or Low relative to the window's centre. " +
        "The application's own descriptions of what a phase means are inconsistent, so no meaning is attached to a phase. " +
        $"period is the moving-average window in bars (default {ChartConstants.DefaultReverseWatchPeriod}, the chart's default). isMaBased (default true) averages the price and volume over the period; false uses the close and volume of each bar. " +
        "isLogScaleVolume (default false) takes log10 of the volume. " +
        $"dataCount (default {ChartConstants.DefaultReverseWatchDataCount}) is the number of latest points the window's centre and bounds are taken from. " +
        $"The calculation runs over up to lookbackBars stored bars (default and maximum {_limits.MaxCandlesPerRequest}); the last outputTailCount points are returned (default {_limits.DefaultOutputTailBars}, at most {_limits.MaxOutputTailBars}). " +
        "The tool never changes anything and never fetches data.";

    public bool IsHeavy => false;

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars. Supported: {ToolArgs.SupportedTimeFrameNames}.", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(PeriodArgument, $"Moving-average window in bars, 1 to {_limits.MaxCandlesPerRequest}. Default {ChartConstants.DefaultReverseWatchPeriod}.", defaultValue: ChartConstants.DefaultReverseWatchPeriod, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Boolean(MaBasedArgument, $"true (default): average the price and volume over the period; false: use the close and volume of each bar.")
        .Boolean(LogVolumeArgument, "Take log10 of the volume before the curve is built. Default false.")
        .Integer(DataCountArgument, $"Latest points the window's centre and bounds are taken from, 2 to {_limits.MaxCandlesPerRequest}. Default {ChartConstants.DefaultReverseWatchDataCount}.", defaultValue: ChartConstants.DefaultReverseWatchDataCount, minimum: 2, maximum: _limits.MaxCandlesPerRequest)
        .Integer(LookbackArgument, $"Stored bars the calculation runs over, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(TailArgument, $"Number of trailing points returned. Default {_limits.DefaultOutputTailBars}, at most {_limits.MaxOutputTailBars}.", defaultValue: _limits.DefaultOutputTailBars, minimum: 1, maximum: _limits.MaxOutputTailBars)
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string symbol = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, cancellationToken).ConfigureAwait(false);
        TimeFrame timeFrame = ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument);
        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);
        int period = ToolArgs.PositiveWithin(arguments, PeriodArgument, ChartConstants.DefaultReverseWatchPeriod, _limits.MaxCandlesPerRequest);
        int dataCount = ToolArgs.PositiveWithin(arguments, DataCountArgument, ChartConstants.DefaultReverseWatchDataCount, _limits.MaxCandlesPerRequest);
        if (dataCount < 2)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{DataCountArgument}' must be at least 2.", DataCountArgument);
        }

        bool isMaBased = ToolArgs.Boolean(arguments, MaBasedArgument) ?? DefaultMaBased;
        bool isLogScaleVolume = ToolArgs.Boolean(arguments, LogVolumeArgument) ?? DefaultLogScaleVolume;
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest);
        if (lookback < period + dataCount)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{LookbackArgument}' ({lookback}) must be at least period + dataCount ({period + dataCount}).", LookbackArgument);
        }

        int tail = ToolArgs.PositiveWithin(arguments, TailArgument, _limits.DefaultOutputTailBars, _limits.MaxOutputTailBars);

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

                throw new ToolException(ToolErrorCodes.InsufficientMarketData, $"No stored {timeFrame} bars exist on or before {endDate:yyyy-MM-dd} for {symbol}.", EndDateArgument);
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

        List<CoreCandleData> candles = loaded.Select(bar => new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume)).ToList();
        cancellationToken.ThrowIfCancellationRequested();

        ReverseWatchCurveData curve = _gateway.CalculateReverseWatch(candles, period, symbol, isMaBased, isLogScaleVolume, dataCount)
            ?? throw new ToolException(
                ToolErrorCodes.InsufficientMarketData,
                $"The Reverse Watch curve with period {period} needs at least {period} stored {timeFrame} bars; {symbol} has {loaded.Count} in the lookback window.",
                PeriodArgument,
                $"Use a period of {loaded.Count} or less, or a larger lookbackBars.");

        return ToolJson.Build(writer => WriteResult(writer, symbol, timeFrame, loaded.Count, curve, tail, period, isMaBased, isLogScaleVolume, dataCount, endDate));
    }

    private static void WriteResult(
        Utf8JsonWriter writer,
        string symbol,
        TimeFrame timeFrame,
        int barCount,
        ReverseWatchCurveData curve,
        int tail,
        int period,
        bool isMaBased,
        bool isLogScaleVolume,
        int dataCount,
        DateOnly? endDate)
    {
        writer.WriteStartObject();
        writer.WriteString("symbol", symbol);
        writer.WriteString("timeFrame", timeFrame.ToString());
        if (endDate is not null)
        {
            writer.WriteString("endDate", endDate.Value.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
        }

        writer.WriteNumber("calculatedBarCount", barCount);
        writer.WriteNumber("pointCount", curve.Points.Count);
        writer.WriteNumber("period", period);
        writer.WriteBoolean("isMaBased", isMaBased);
        writer.WriteBoolean("isLogScaleVolume", isLogScaleVolume);
        writer.WriteNumber("dataCount", dataCount);

        var activePoints = curve.Points.Skip(Math.Max(0, curve.Points.Count - dataCount)).ToList();
        decimal rawMinPrice = activePoints.Count > 0 ? activePoints.Min(p => p.PriceAverage) : curve.Bounds.MinPrice;
        decimal rawMaxPrice = activePoints.Count > 0 ? activePoints.Max(p => p.PriceAverage) : curve.Bounds.MaxPrice;
        decimal rawMinVolume = activePoints.Count > 0 ? activePoints.Min(p => p.VolumeAverage) : curve.Bounds.MinVolume;
        decimal rawMaxVolume = activePoints.Count > 0 ? activePoints.Max(p => p.VolumeAverage) : curve.Bounds.MaxVolume;
        decimal centerPrice = (rawMinPrice + rawMaxPrice) / 2m;
        decimal centerVolume = (rawMinVolume + rawMaxVolume) / 2m;
        bool isSingularity = rawMinPrice == rawMaxPrice && rawMinVolume == rawMaxVolume;

        writer.WriteStartObject("bounds");
        writer.WriteNumber("minPrice", curve.Bounds.MinPrice);
        writer.WriteNumber("maxPrice", curve.Bounds.MaxPrice);
        writer.WriteNumber("rawMinPrice", rawMinPrice);
        writer.WriteNumber("rawMaxPrice", rawMaxPrice);
        writer.WriteNumber("centerPrice", centerPrice);
        writer.WriteNumber("minVolume", curve.Bounds.MinVolume);
        writer.WriteNumber("maxVolume", curve.Bounds.MaxVolume);
        writer.WriteNumber("rawMinVolume", rawMinVolume);
        writer.WriteNumber("rawMaxVolume", rawMaxVolume);
        writer.WriteNumber("centerVolume", centerVolume);
        writer.WriteString("volumeUnit", isLogScaleVolume ? "log10" : "linear");
        writer.WriteEndObject();

        writer.WriteStartArray("recent");
        foreach (ReverseWatchCurvePoint point in curve.Points.Skip(Math.Max(0, curve.Points.Count - tail)))
        {
            writer.WriteStartObject();
            writer.WriteString("date", ToolJson.FormatDate(point.Date));
            if (isSingularity)
            {
                writer.WriteNumber("angle", 0m);
                writer.WriteNumber("phaseNumber", 0);
                writer.WriteString("phase", "Phase0");
                writer.WriteString("phaseDescription", "Neutral (No Variation)");
                writer.WriteString("pricePosition", ReverseWatchPhaseGeometry.Middle);
                writer.WriteString("volumePosition", ReverseWatchPhaseGeometry.Middle);
            }
            else
            {
                writer.WriteString("phase", point.Phase.ToString());
                if (ReverseWatchPhaseGeometry.TryGet(point.Phase, out string pricePosition, out string volumePosition))
                {
                    writer.WriteString("pricePosition", pricePosition);
                    writer.WriteString("volumePosition", volumePosition);
                }
            }

            writer.WriteNumber("priceAverage", point.PriceAverage);
            writer.WriteNumber("volumeAverage", point.VolumeAverage);
            writer.WriteNumber("close", point.Close);
            writer.WriteNumber("volume", point.Volume);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
