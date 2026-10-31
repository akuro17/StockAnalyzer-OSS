using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_analyze_cycles: analyzes cyclical price components and dominant frequency peaks
/// using pure Fast Fourier Transform (FFT) spectrum analysis in a strictly causal, prefix-invariant manner.
/// </summary>
internal sealed class AnalyzeCyclesTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string TopBinsArgument = "topBins";
    private const string RangeBarsArgument = "rangeBars";

    private const int DefaultTopBins = 10;

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;

    public AnalyzeCyclesTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string Name => "sa_analyze_cycles";

    public string Description =>
        "Analyzes cyclical price components and dominant frequencies using pure Fast Fourier Transform (FFT) spectrum analysis. " +
        "Output periods are in bars. Strictly causal and prefix-invariant: historical cycles never depend on future bars.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"topBins\":10}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(LookbackArgument, $"Stored bars loaded up to endDate, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(TopBinsArgument, $"Number of dominant frequency bins to return (1..{_limits.MaxCycleBins}). Default {DefaultTopBins}.", defaultValue: DefaultTopBins, minimum: 1, maximum: _limits.MaxCycleBins)
        .Integer(RangeBarsArgument, $"Number of trailing bars to analyze from the loaded window (2..{_limits.MaxCandlesPerRequest}). Default: all loaded bars.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 2, maximum: _limits.MaxCandlesPerRequest)
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
        int topBins = ToolArgs.PositiveWithin(arguments, TopBinsArgument, DefaultTopBins, _limits.MaxCycleBins);

        int? rangeBars = arguments.TryGetProperty(RangeBarsArgument, out _)
            ? ToolArgs.PositiveWithin(arguments, RangeBarsArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest)
            : null;
        if (rangeBars.HasValue)
        {
            if (rangeBars.Value < 2)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{RangeBarsArgument} must be >= 2.", RangeBarsArgument);
            }
            if (rangeBars.Value > lookback)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"{RangeBarsArgument} cannot exceed {LookbackArgument}.", RangeBarsArgument);
            }
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

        double[] samples = subset.Select(c => (double)c.Close).ToArray();
        FftSpectrumResult result = FftSpectrumAnalysis.CalculateSpectrum(samples);

        IReadOnlyList<FftSpectrumBin> topBinsList = (result.Bins.Count > 0)
            ? result.Bins.OrderByDescending(b => b.Power).ThenBy(b => b.Period).Take(topBins).ToList()
            : Array.Empty<FftSpectrumBin>();

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", symbol);
            writer.WriteString("timeFrame", timeFrame.ToString());
            writer.WriteString("asOfDate", ToolJson.FormatDate(subset[^1].Timestamp));
            writer.WriteNumber("barCount", subset.Count);

            if (result.DominantBin is not null && result.Bins.Count > 0)
            {
                writer.WriteNumber("dominantPeriodBars", result.DominantPeriod);
                writer.WriteNumber("dominantPower", result.DominantPower);
            }
            else
            {
                writer.WriteNull("dominantPeriodBars");
                writer.WriteNull("dominantPower");
            }

            writer.WriteStartArray("bins");
            foreach (FftSpectrumBin bin in topBinsList)
            {
                writer.WriteStartObject();
                writer.WriteNumber("periodBars", bin.Period);
                writer.WriteNumber("power", bin.Power);
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
}
