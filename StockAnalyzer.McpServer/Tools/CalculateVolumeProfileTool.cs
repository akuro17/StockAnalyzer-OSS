using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_calculate_volume_profile: calculates price-level volume distribution (Volume Profile) or residence time distribution (Time-at-Price)
/// over a window of candles, identifying Point of Control (POC), Value Area High (VAH), and Value Area Low (VAL).
/// </summary>
internal sealed class CalculateVolumeProfileTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string BasisArgument = "basis";
    private const string RangeBarsArgument = "rangeBars";
    private const string BinCountArgument = "binCount";
    private const string ValueAreaPercentArgument = "valueAreaPercent";

    private static readonly string[] SupportedBases = { "Volume", "TimeAtPrice" };

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;

    public CalculateVolumeProfileTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string Name => "sa_calculate_volume_profile";

    public string Description =>
        "Calculates price-level volume distribution (Volume Profile) or residence time distribution (Time-at-Price) " +
        "over a specified lookback range, deriving Point of Control (POC), Value Area High (VAH), and Value Area Low (VAL). " +
        "Operates strictly read-only and respects causal as-of dates.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"basis\":\"Volume\",\"binCount\":24,\"valueAreaPercent\":0.7}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(LookbackArgument, $"Stored bars loaded up to endDate, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .String(BasisArgument, "Calculation basis: 'Volume' (distributes traded volume across touched bins) or 'TimeAtPrice' (distributes bar residence time). Default Volume.", allowedValues: SupportedBases)
        .Integer(RangeBarsArgument, $"Number of trailing bars from the loaded window to calculate profile over. Default uses all loaded bars (at most {_limits.MaxCandlesPerRequest}).", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(BinCountArgument, $"Number of vertical price bins (2..{_limits.MaxVolumeProfileBins}). Default {ChartConstants.DefaultVolumeProfileRowSize}.", defaultValue: ChartConstants.DefaultVolumeProfileRowSize, minimum: 2, maximum: _limits.MaxVolumeProfileBins)
        .Number(ValueAreaPercentArgument, $"Target fraction of total profile volume inside the value area (0.0 < valueAreaPercent <= 1.0). Default {ChartConstants.DefaultValueAreaPercent}.")
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

        string rawBasis = ToolArgs.String(arguments, BasisArgument) ?? "Volume";
        if (!SupportedBases.Contains(rawBasis, StringComparer.OrdinalIgnoreCase))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{BasisArgument} must be 'Volume' or 'TimeAtPrice'.", BasisArgument);
        }
        string basis = rawBasis.Equals("TimeAtPrice", StringComparison.OrdinalIgnoreCase) ? "TimeAtPrice" : "Volume";

        int binCount = ToolArgs.PositiveWithin(arguments, BinCountArgument, ChartConstants.DefaultVolumeProfileRowSize, _limits.MaxVolumeProfileBins);
        if (binCount < 2)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{BinCountArgument} must be between 2 and {_limits.MaxVolumeProfileBins}.", BinCountArgument);
        }

        double valueAreaPercent = ToolArgs.Number(arguments, ValueAreaPercentArgument) ?? ChartConstants.DefaultValueAreaPercent;
        if (valueAreaPercent <= 0.0 || valueAreaPercent > 1.0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{ValueAreaPercentArgument} must be > 0.0 and <= 1.0.", ValueAreaPercentArgument);
        }

        int? rangeBars = arguments.TryGetProperty(RangeBarsArgument, out _)
            ? ToolArgs.PositiveWithin(arguments, RangeBarsArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest)
            : null;
        if (rangeBars.HasValue && rangeBars.Value < 1)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{RangeBarsArgument} must be >= 1.", RangeBarsArgument);
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

        var subset = loaded;
        if (rangeBars.HasValue && rangeBars.Value < loaded.Count)
        {
            subset = loaded.Skip(loaded.Count - rangeBars.Value).ToList();
        }

        decimal minPrice = subset.Min(c => c.Low);
        decimal maxPrice = subset.Max(c => c.High);

        List<CoreCandleData> coreSubset = subset.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToList();

        List<VolumeBin> bins;
        decimal vah;
        decimal val;
        decimal pocPrice;

        if (minPrice == maxPrice)
        {
            // Range 0 edge case: degenerate to single bin
            long totalVolume = 0;
            if (basis == "TimeAtPrice")
            {
                totalVolume = coreSubset.Count;
            }
            else
            {
                checked
                {
                    for (int i = 0; i < coreSubset.Count; i++)
                    {
                        totalVolume += coreSubset[i].Volume;
                    }
                }
            }

            bins = new List<VolumeBin>
            {
                new VolumeBin
                {
                    LowerBound = minPrice,
                    UpperBound = maxPrice,
                    Price = minPrice,
                    TotalVolume = totalVolume,
                    BuyVolume = 0,
                    SellVolume = 0,
                    WidthPercent = 1.0
                }
            };
            vah = minPrice;
            val = minPrice;
            pocPrice = minPrice;
        }
        else
        {
            if (basis == "TimeAtPrice")
            {
                bins = TimeAtPriceAnalysis.CalculateProfile(coreSubset, binCount, PriceType.Close);
                (vah, val) = TimeAtPriceAnalysis.CalculateValueArea(bins, valueAreaPercent);
            }
            else
            {
                bins = VolumeAnalysis.CalculateProfile(coreSubset, binCount, VolumeDistributionMode.Proportional);
                (vah, val) = VolumeAnalysis.CalculateValueArea(bins, valueAreaPercent);
            }

            pocPrice = 0m;
            long maxVol = -1;
            for (int i = 0; i < bins.Count; i++)
            {
                if (bins[i].TotalVolume > maxVol)
                {
                    maxVol = bins[i].TotalVolume;
                    pocPrice = bins[i].Price;
                }
            }
        }

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
            writer.WriteString("rangeStart", subset[0].Timestamp.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
            writer.WriteString("rangeEnd", subset[^1].Timestamp.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
            writer.WriteNumber("barCount", subset.Count);
            writer.WriteNumber("priceMin", minPrice);
            writer.WriteNumber("priceMax", maxPrice);
            writer.WriteString("basis", basis);
            writer.WriteNumber("binCount", bins.Count);

            writer.WriteStartArray("bins");
            foreach (var b in bins)
            {
                writer.WriteStartObject();
                writer.WriteNumber("priceLow", b.LowerBound);
                writer.WriteNumber("priceHigh", b.UpperBound);
                writer.WriteNumber("volume", b.TotalVolume);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartObject("poc");
            writer.WriteNumber("price", pocPrice);
            writer.WriteEndObject();

            writer.WriteStartObject("valueArea");
            writer.WriteNumber("high", vah);
            writer.WriteNumber("low", val);
            writer.WriteNumber("percent", valueAreaPercent);
            writer.WriteEndObject();

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
