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
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_get_seasonality: extracts the historical calendar seasonality matrix and monthly fractional return statistics for a symbol.
/// </summary>
internal sealed class GetSeasonalityTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string StartMonthArgument = "startMonth";
    private const string YearsArgument = "years";

    private const int DefaultStartMonth = 1;
    private const int DefaultYears = SeasonalityChartConstants.SeasonalityBasePaletteSize;

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;
    private readonly bool _compactDefaults;

    public GetSeasonalityTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits, bool compactDefaults = false)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _compactDefaults = compactDefaults;
    }

    public string Name => "sa_get_seasonality";

    public string Description =>
        "Extracts the historical calendar seasonality matrix and monthly fractional return statistics for a symbol. " +
        "Output contains raw monthly return ratios and averages; no advice or win-rate is synthesized. " +
        (_compactDefaults
            ? $"{SeriesOutputOptions.DecimalPlacesArgument} (0 to {SeriesOutputOptions.MaxDecimalPlaces}, optional) rounds returnRatio and average (away from zero at the midpoint; output only); default {SeriesOutputOptions.DefaultCompactDecimalPlaces} decimal places."
            : $"{SeriesOutputOptions.DecimalPlacesArgument} (0 to {SeriesOutputOptions.MaxDecimalPlaces}, optional) rounds returnRatio and average (away from zero at the midpoint; output only); without it the ratios are as calculated.");

    public bool IsHeavy => false;

    public IReadOnlyDictionary<string, string>? MisnamedArgumentHints => DateArgumentHints.ForEndDate;

    public string? CompactUsageHint => "Pass decimalPlaces: 4 or fewer years.";

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"years\":10,\"startMonth\":1}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(LookbackArgument, $"Stored bars loaded up to endDate, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(StartMonthArgument, "Starting calendar month for the 1-year cycle (1..12). Default 1.", defaultValue: DefaultStartMonth, minimum: 1, maximum: 12)
        .Integer(YearsArgument, $"Number of overlaid calendar years to analyze (1..{_limits.MaxSeasonalityYears}). Default {DefaultYears}.", defaultValue: DefaultYears, minimum: 1, maximum: _limits.MaxSeasonalityYears)
        .Boolean(SymbolResolver.AllowUnregisteredArgument, "Accept a symbol that is not in the ticker list but has stored bars. Default false.")
        .Integer(SeriesOutputOptions.DecimalPlacesArgument, SeriesOutputOptions.DecimalPlacesDescription("returnRatio and average", _compactDefaults), defaultValue: _compactDefaults ? SeriesOutputOptions.DefaultCompactDecimalPlaces : null, minimum: 0, maximum: SeriesOutputOptions.MaxDecimalPlaces)
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

        long? rawStartMonth = ToolArgs.Integer(arguments, StartMonthArgument);
        if (rawStartMonth.HasValue && (rawStartMonth.Value < 1 || rawStartMonth.Value > 12))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{StartMonthArgument}' must be between 1 and 12.", StartMonthArgument);
        }
        int startMonth = (int)(rawStartMonth ?? DefaultStartMonth);

        int years = ToolArgs.PositiveWithin(arguments, YearsArgument, Math.Min(DefaultYears, _limits.MaxSeasonalityYears), _limits.MaxSeasonalityYears);
        int? decimalPlaces = SeriesOutputOptions.ReadDecimalPlaces(arguments, _compactDefaults ? SeriesOutputOptions.DefaultCompactDecimalPlaces : null);

        DateTime? referenceDate = await _loader.ReferenceDateAsync(symbol, endDate, cancellationToken).ConfigureAwait(false);

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

        var points = new List<SeasonalityPoint>(loaded.Count);
        DateTime? lastTs = null;
        foreach (var candle in loaded)
        {
            if (lastTs is null || candle.Timestamp > lastTs.Value)
            {
                points.Add(new SeasonalityPoint(candle.Timestamp, candle.Close));
                lastTs = candle.Timestamp;
            }
        }

        var seriesInput = new SeasonalitySeriesInput(0, symbol, SeasonalityRadiusMode.PercentVsYearStart, points);
        var parameters = new SeasonalityChartParameters((uint)years, StartMonth: (uint)startMonth);
        SeasonalityChartResult chartResult = SeasonalityChartEngine.Analyze(new[] { seriesInput }, parameters);
        SeasonalityMonthlyReturnsTable table = SeasonalityMonthlyReturnsEngine.Build(chartResult);

        string[] abbreviatedMonths = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames;
        DateTime lastBarTs = loaded[^1].Timestamp;
        DateTime lastBarPeriodEnd = TimeFrameAggregator.PeriodEnd(lastBarTs, TimeFrame.MN1);
        bool lastBarPartial = referenceDate is { } refDt && lastBarPeriodEnd.Date > refDt.Date;
        int lastBarMonth = lastBarTs.Month;
        int lastBarCycleYear = startMonth <= 1 || lastBarMonth >= startMonth ? lastBarTs.Year : lastBarTs.Year - 1;

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", symbol);
            writer.WriteString("timeFrame", timeFrame.ToString());
            writer.WriteString("asOfDate", ToolJson.FormatDate(loaded[^1].Timestamp));
            writer.WriteNumber("startMonth", startMonth);
            PartialBarOutput.Write(writer, referenceDate, lastBarPeriodEnd, lastBarPartial, droppedPartialBar: false);

            writer.WriteStartArray("years");
            foreach (int y in table.Years)
            {
                writer.WriteNumberValue(y);
            }
            writer.WriteEndArray();

            writer.WriteStartArray("rows");
            for (int r = 0; r < SeasonalityMonthlyReturnsTable.RowCount; r++)
            {
                bool isAnnual = r == SeasonalityMonthlyReturnsTable.AnnualRowIndex;
                string label = isAnnual
                    ? "Annual"
                    : abbreviatedMonths[(int)(((startMonth - 1 + r) % 12))];

                writer.WriteStartObject();
                writer.WriteString("label", label);

                writer.WriteStartArray("cells");
                for (int y = 0; y < table.Years.Count; y++)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("year", table.Years[y]);
                    decimal? ret = table.Cell(r, y).Return;
                    if (ret.HasValue)
                    {
                        writer.WriteNumber("returnRatio", SeriesOutputOptions.RoundIfRequested(ret.Value, decimalPlaces));
                    }
                    else
                    {
                        writer.WriteNull("returnRatio");
                    }

                    if (lastBarPartial && ret.HasValue && table.Years[y] == lastBarCycleYear)
                    {
                        int cellMonth = ((startMonth - 1 + r) % 12) + 1;
                        if (isAnnual || cellMonth == lastBarMonth)
                        {
                            writer.WriteBoolean("partial", true);
                        }
                    }

                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                decimal? avg = table.RowAverages[r];
                if (avg.HasValue)
                {
                    writer.WriteNumber("average", SeriesOutputOptions.RoundIfRequested(avg.Value, decimalPlaces));
                }
                else
                {
                    writer.WriteNull("average");
                }

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
