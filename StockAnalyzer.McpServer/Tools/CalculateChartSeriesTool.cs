using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_calculate_chart_series: numerical series, effective sizing, DataWindow metrics, and optional pattern analysis
/// for Heikin-Ashi and Non-Time-Series charts (HeikinAshi, Renko, Kagi, PointAndFigure, ThreeLineBreak).
/// </summary>
internal sealed class CalculateChartSeriesTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string ChartTypeArgument = "chartType";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string TailArgument = "outputTailCount";

    // Sizing arguments
    private const string SizingModeArgument = "sizingMode";
    private const string ReversalModeArgument = "reversalMode";
    private const string ManualSizeArgument = "manualSize";
    private const string BrickSizeArgument = "brickSize";
    private const string BoxSizeArgument = "boxSize";
    private const string ReversalAmountArgument = "reversalAmount";
    private const string PercentageArgument = "percentage";
    private const string BrickPercentArgument = "brickPercent";
    private const string BoxPercentArgument = "boxPercent";
    private const string ReversalPercentArgument = "reversalPercent";
    private const string AtrPeriodArgument = "atrPeriod";
    private const string AtrMultiplierArgument = "atrMultiplier";
    private const string RoundingModeArgument = "roundingMode";
    private const string FallbackModeArgument = "fallbackMode";
    private const string ReversalArgument = "reversal";
    private const string InitialColumnArgument = "initialColumn";

    // Three Line Break arguments
    private const string LineCountArgument = "lineCount";
    private const string MinimumMoveArgument = "minimumMove";
    private const string ToleranceArgument = "tolerance";

    // Pattern toggles
    private const string IncludeMultiWaveArgument = "includeMultiWave";
    private const string IncludePatternsArgument = "includePatterns";
    private const string ShowDoubleBreakoutArgument = "showDoubleBreakout";
    private const string ShowTripleBreakoutArgument = "showTripleBreakout";
    private const string ShowTrendlineBreakoutArgument = "showTrendlineBreakout";
    private const string ShowTriangleBreakoutArgument = "showTriangleBreakout";
    private const string ShowCatapultBreakoutArgument = "showCatapultBreakout";

    private static readonly string[] SupportedChartTypes =
    {
        nameof(ChartType.HeikinAshi),
        nameof(ChartType.Renko),
        nameof(ChartType.Kagi),
        nameof(ChartType.PointAndFigure),
        nameof(ChartType.ThreeLineBreak)
    };

    private static readonly string[] SizingModes = Enum.GetNames<ChartSizingMode>();
    private static readonly string[] RoundingModes = Enum.GetNames<ChartRoundingMode>();
    private static readonly string[] FallbackModes = Enum.GetNames<AutoFallbackMode>();

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;

    public CalculateChartSeriesTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway;
        _symbols = symbols;
        _loader = new CandleWindowLoader(gateway);
        _limits = limits;
    }

    public string Name => "sa_calculate_chart_series";

    public string Description =>
        "Calculates the converted numerical series, effective sizing parameters, DataWindow metrics, and optional pattern analysis " +
        "for Heikin-Ashi and Non-Time-Series charts (HeikinAshi, Renko, Kagi, PointAndFigure, ThreeLineBreak). " +
        "Supports all GUI calculation settings (sizing modes, manual sizes, percentages, ATR period/multiplier, rounding and fallback modes, reversals, initial column, line count, minimum move, tolerance, multi-wave and P&F patterns). " +
        "Never mutates data and runs read-only.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"chartType\":\"HeikinAshi\",\"outputTailCount\":200}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars. Supported: {ToolArgs.SupportedTimeFrameNames}.", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(ChartTypeArgument, $"Target chart type. Supported: {string.Join(", ", SupportedChartTypes)}.", required: true, allowedValues: SupportedChartTypes)
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(LookbackArgument, $"Stored bars the calculation runs over, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Integer(TailArgument, $"Number of trailing converted elements returned. Default {_limits.DefaultOutputTailBars}, at most {_limits.MaxOutputTailBars}.", defaultValue: _limits.DefaultOutputTailBars, minimum: 1, maximum: _limits.MaxOutputTailBars)
        .String(SizingModeArgument, $"Sizing mode for Renko, Kagi, P&F. Supported: {string.Join(", ", SizingModes)}. Default AutoAtr.", allowedValues: SizingModes)
        .String(ReversalModeArgument, $"Alias for sizingMode on Kagi charts. Supported: {string.Join(", ", SizingModes)}.", allowedValues: SizingModes)
        .Number(ManualSizeArgument, "Manual size (BrickSize/BoxSize/ReversalAmount) when SizingMode is Fixed or fallback Fixed. Must be > 0. Default 1.0.")
        .Number(BrickSizeArgument, "Alias for manualSize on Renko charts. Must be > 0.")
        .Number(BoxSizeArgument, "Alias for manualSize on PointAndFigure charts. Must be > 0.")
        .Number(ReversalAmountArgument, "Alias for manualSize on Kagi charts. Must be > 0.")
        .Number(PercentageArgument, "Percentage size when SizingMode is Percentage or fallback Percentage. Must be in 0.01..100. Default 1.0.")
        .Number(BrickPercentArgument, "Alias for percentage on Renko charts. Must be in 0.01..100.")
        .Number(BoxPercentArgument, "Alias for percentage on PointAndFigure charts. Must be in 0.01..100.")
        .Number(ReversalPercentArgument, "Alias for percentage on Kagi charts. Must be in 0.01..100.")
        .Integer(AtrPeriodArgument, $"ATR period for AutoAtr sizing on Renko, Kagi, P&F. Default {ChartConstants.DefaultAtrPeriod}, range 1..500.", defaultValue: ChartConstants.DefaultAtrPeriod, minimum: 1, maximum: 500)
        .Number(AtrMultiplierArgument, $"ATR multiplier for AutoAtr sizing on Renko, Kagi, P&F. Default {ChartConstants.DefaultAtrMultiplier}, must be > 0.")
        .String(RoundingModeArgument, $"Rounding mode for Renko, Kagi, P&F. Supported: {string.Join(", ", RoundingModes)}. Default None.", allowedValues: RoundingModes)
        .String(FallbackModeArgument, $"Fallback mode when ATR is 0 or insufficient on Renko, Kagi, P&F. Supported: {string.Join(", ", FallbackModes)}. Default Percentage.", allowedValues: FallbackModes)
        .Integer(ReversalArgument, $"Reversal requirement in bricks/boxes (Renko default 2, P&F default {ChartConstants.DefaultReversalAmount}, range 1..10).", defaultValue: 2, minimum: 1, maximum: 10)
        .Integer(InitialColumnArgument, "Initial column index offset for Kagi charts (default 0, range 0..10000).", defaultValue: 0, minimum: 0, maximum: 10000)
        .Integer(LineCountArgument, $"Line count requirement for ThreeLineBreak charts (default {ChartConstants.DefaultThreeLineBreakLineCount}, range 1..20).", defaultValue: ChartConstants.DefaultThreeLineBreakLineCount, minimum: 1, maximum: 20)
        .Number(MinimumMoveArgument, "Minimum movement threshold for ThreeLineBreak charts (default 0). Must be >= 0.")
        .Number(ToleranceArgument, $"Tolerance for trend detection in ThreeLineBreak charts (default {ChartConstants.ThreeLineBreakTrendThreshold}). Must be >= 0.")
        .Boolean(IncludeMultiWaveArgument, "Include Multi-Wave pattern analysis on converted elements (Renko, Kagi, P&F, ThreeLineBreak). Default false.")
        .Boolean(IncludePatternsArgument, "Include Point and Figure pattern analysis (P&F only). Default false.")
        .Boolean(ShowDoubleBreakoutArgument, "Include Double Top/Bottom breakout signals in P&F pattern analysis. Default true.")
        .Boolean(ShowTripleBreakoutArgument, "Include Triple Top/Bottom breakout signals in P&F pattern analysis. Default true.")
        .Boolean(ShowTrendlineBreakoutArgument, "Include Trendline breakout signals in P&F pattern analysis. Default true.")
        .Boolean(ShowTriangleBreakoutArgument, "Include Triangle breakout signals in P&F pattern analysis. Default true.")
        .Boolean(ShowCatapultBreakoutArgument, "Include Catapult breakout signals in P&F pattern analysis. Default true.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string symbol = await _symbols.ResolveAsync(ToolArgs.String(arguments, SymbolArgument)!, SymbolArgument, cancellationToken).ConfigureAwait(false);
        TimeFrame timeFrame = ToolArgs.SupportedTimeFrame(arguments, TimeFrameArgument);
        ChartType chartType = ParseChartType(arguments);

        DateOnly? endDate = ToolArgs.Date(arguments, EndDateArgument);
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.MaxCandlesPerRequest, _limits.MaxCandlesPerRequest);
        int tail = ToolArgs.PositiveWithin(arguments, TailArgument, _limits.DefaultOutputTailBars, _limits.MaxOutputTailBars);

        ChartSeriesRequest request = BuildRequest(arguments, chartType);

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

        var coreCandles = loaded.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
        cancellationToken.ThrowIfCancellationRequested();

        ChartSeriesCalculationResult result;
        try
        {
            result = _gateway.CalculateChartSeries(coreCandles, request, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, ex.Message, ChartTypeArgument);
        }

        return ToolJson.Build(writer => WriteResult(writer, symbol, timeFrame, chartType, loaded.Count, result, tail, endDate));
    }

    private static ChartType ParseChartType(JsonElement arguments)
    {
        string? name = ToolArgs.String(arguments, ChartTypeArgument);
        if (string.IsNullOrWhiteSpace(name) || !Enum.TryParse<ChartType>(name, ignoreCase: true, out var chartType) || !SupportedChartTypes.Contains(chartType.ToString()))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Chart type '{name}' is not supported.", ChartTypeArgument, $"Supported: {string.Join(", ", SupportedChartTypes)}.");
        }

        return chartType;
    }

    private static ChartSeriesRequest BuildRequest(JsonElement arguments, ChartType chartType)
    {
        // 1. Sizing Mode & Alias
        bool hasSizingMode = HasProperty(arguments, SizingModeArgument);
        bool hasReversalMode = HasProperty(arguments, ReversalModeArgument);

        if (hasSizingMode && hasReversalMode)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Specify either '{SizingModeArgument}' or '{ReversalModeArgument}', not both.", ReversalModeArgument);
        }

        if (hasReversalMode && chartType != ChartType.Kagi)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{ReversalModeArgument}' is only valid for Kagi charts.", ReversalModeArgument);
        }

        if (hasSizingMode && chartType is ChartType.HeikinAshi or ChartType.ThreeLineBreak)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{SizingModeArgument}' is not applicable to {chartType}.", SizingModeArgument);
        }

        // 2. Manual Size & Aliases
        string[] sizeAliases = { ManualSizeArgument, BrickSizeArgument, BoxSizeArgument, ReversalAmountArgument };
        var presentSizes = sizeAliases.Where(a => HasProperty(arguments, a)).ToList();
        if (presentSizes.Count > 1)
        {
            string conflict = presentSizes.First(a => a != ManualSizeArgument);
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Conflicting size parameters specified: {string.Join(", ", presentSizes)}.", conflict);
        }

        if (HasProperty(arguments, BrickSizeArgument) && chartType != ChartType.Renko)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{BrickSizeArgument}' is only valid for Renko charts.", BrickSizeArgument);
        if (HasProperty(arguments, BoxSizeArgument) && chartType != ChartType.PointAndFigure)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{BoxSizeArgument}' is only valid for PointAndFigure charts.", BoxSizeArgument);
        if (HasProperty(arguments, ReversalAmountArgument) && chartType != ChartType.Kagi)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{ReversalAmountArgument}' is only valid for Kagi charts.", ReversalAmountArgument);
        if (HasProperty(arguments, ManualSizeArgument) && chartType is ChartType.HeikinAshi or ChartType.ThreeLineBreak)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{ManualSizeArgument}' is not applicable to {chartType}.", ManualSizeArgument);

        decimal manualSize = 1.0m;
        if (presentSizes.Count == 1)
        {
            string sizeProp = presentSizes[0];
            manualSize = GetDecimalValue(arguments, sizeProp);
            if (manualSize <= 0m)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{sizeProp}' must be greater than 0.", sizeProp);
            }
        }

        // 3. Percentage & Aliases
        string[] percentAliases = { PercentageArgument, BrickPercentArgument, BoxPercentArgument, ReversalPercentArgument };
        var presentPercents = percentAliases.Where(a => HasProperty(arguments, a)).ToList();
        if (presentPercents.Count > 1)
        {
            string conflict = presentPercents.First(a => a != PercentageArgument);
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Conflicting percentage parameters specified: {string.Join(", ", presentPercents)}.", conflict);
        }

        if (HasProperty(arguments, BrickPercentArgument) && chartType != ChartType.Renko)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{BrickPercentArgument}' is only valid for Renko charts.", BrickPercentArgument);
        if (HasProperty(arguments, BoxPercentArgument) && chartType != ChartType.PointAndFigure)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{BoxPercentArgument}' is only valid for PointAndFigure charts.", BoxPercentArgument);
        if (HasProperty(arguments, ReversalPercentArgument) && chartType != ChartType.Kagi)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{ReversalPercentArgument}' is only valid for Kagi charts.", ReversalPercentArgument);
        if (HasProperty(arguments, PercentageArgument) && chartType is ChartType.HeikinAshi or ChartType.ThreeLineBreak)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{PercentageArgument}' is not applicable to {chartType}.", PercentageArgument);

        decimal percentage = 1.0m;
        if (presentPercents.Count == 1)
        {
            string pctProp = presentPercents[0];
            percentage = GetDecimalValue(arguments, pctProp);
            if (percentage < 0.01m || percentage > 100m)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{pctProp}' must be between 0.01 and 100.", pctProp);
            }
        }

        // Check mutual exclusivity of manual size and percentage
        if (presentSizes.Count > 0 && presentPercents.Count > 0)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Cannot specify both manual size parameter '{presentSizes[0]}' and percentage parameter '{presentPercents[0]}'.", presentPercents[0]);
        }

        ChartSizingMode sizingMode;
        if (hasSizingMode || hasReversalMode)
        {
            sizingMode = hasSizingMode
                ? Enum.Parse<ChartSizingMode>(ToolArgs.String(arguments, SizingModeArgument)!, ignoreCase: true)
                : Enum.Parse<ChartSizingMode>(ToolArgs.String(arguments, ReversalModeArgument)!, ignoreCase: true);

            if (sizingMode == ChartSizingMode.Fixed && presentPercents.Count > 0)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{presentPercents[0]}' cannot be used when sizingMode is Fixed.", presentPercents[0]);
            }

            if (sizingMode == ChartSizingMode.Percentage && presentSizes.Count > 0)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{presentSizes[0]}' cannot be used when sizingMode is Percentage.", presentSizes[0]);
            }
        }
        else
        {
            // Auto-infer sizingMode when omitted
            if (presentSizes.Count > 0)
            {
                sizingMode = ChartSizingMode.Fixed;
            }
            else if (presentPercents.Count > 0)
            {
                sizingMode = ChartSizingMode.Percentage;
            }
            else
            {
                sizingMode = ChartSizingMode.AutoAtr;
            }
        }

        // 4. ATR Period & Multiplier
        if (HasProperty(arguments, AtrPeriodArgument) && chartType is ChartType.HeikinAshi or ChartType.ThreeLineBreak)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{AtrPeriodArgument}' is not applicable to {chartType}.", AtrPeriodArgument);
        if (HasProperty(arguments, AtrMultiplierArgument) && chartType is ChartType.HeikinAshi or ChartType.ThreeLineBreak)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{AtrMultiplierArgument}' is not applicable to {chartType}.", AtrMultiplierArgument);

        int atrPeriod = ToolArgs.PositiveWithin(arguments, AtrPeriodArgument, ChartConstants.DefaultAtrPeriod, 500);

        decimal atrMultiplier = ChartConstants.DefaultAtrMultiplier;
        if (HasProperty(arguments, AtrMultiplierArgument))
        {
            atrMultiplier = GetDecimalValue(arguments, AtrMultiplierArgument);
            if (atrMultiplier <= 0m || atrMultiplier > 100m)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{AtrMultiplierArgument}' must be between 0.01 and 100.", AtrMultiplierArgument);
            }
        }

        // 5. Rounding & Fallback modes
        if (HasProperty(arguments, RoundingModeArgument) && chartType is ChartType.HeikinAshi or ChartType.ThreeLineBreak)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{RoundingModeArgument}' is not applicable to {chartType}.", RoundingModeArgument);
        if (HasProperty(arguments, FallbackModeArgument) && chartType is ChartType.HeikinAshi or ChartType.ThreeLineBreak)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{FallbackModeArgument}' is not applicable to {chartType}.", FallbackModeArgument);

        ChartRoundingMode roundingMode = ChartRoundingMode.None;
        if (HasProperty(arguments, RoundingModeArgument))
        {
            roundingMode = Enum.Parse<ChartRoundingMode>(ToolArgs.String(arguments, RoundingModeArgument)!, ignoreCase: true);
        }

        AutoFallbackMode fallbackMode = AutoFallbackMode.Percentage;
        if (HasProperty(arguments, FallbackModeArgument))
        {
            fallbackMode = Enum.Parse<AutoFallbackMode>(ToolArgs.String(arguments, FallbackModeArgument)!, ignoreCase: true);
        }

        // 6. Reversal & InitialColumn
        if (HasProperty(arguments, ReversalArgument) && chartType is not (ChartType.Renko or ChartType.PointAndFigure))
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{ReversalArgument}' is only applicable to Renko and PointAndFigure charts.", ReversalArgument);

        int reversal = chartType == ChartType.PointAndFigure ? ChartConstants.DefaultReversalAmount : 2;
        if (HasProperty(arguments, ReversalArgument))
        {
            reversal = ToolArgs.PositiveWithin(arguments, ReversalArgument, reversal, 10);
        }

        if (HasProperty(arguments, InitialColumnArgument) && chartType != ChartType.Kagi)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{InitialColumnArgument}' is only applicable to Kagi charts.", InitialColumnArgument);

        int initialColumn = ToolArgs.NonNegativeWithin(arguments, InitialColumnArgument, 0, 10000);

        // 7. ThreeLineBreak specific parameters
        if (HasProperty(arguments, LineCountArgument) && chartType != ChartType.ThreeLineBreak)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{LineCountArgument}' is only applicable to ThreeLineBreak charts.", LineCountArgument);
        if (HasProperty(arguments, MinimumMoveArgument) && chartType != ChartType.ThreeLineBreak)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{MinimumMoveArgument}' is only applicable to ThreeLineBreak charts.", MinimumMoveArgument);
        if (HasProperty(arguments, ToleranceArgument) && chartType != ChartType.ThreeLineBreak)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{ToleranceArgument}' is only applicable to ThreeLineBreak charts.", ToleranceArgument);

        int lineCount = ToolArgs.PositiveWithin(arguments, LineCountArgument, ChartConstants.DefaultThreeLineBreakLineCount, 20);

        decimal minimumMove = 0m;
        if (HasProperty(arguments, MinimumMoveArgument))
        {
            minimumMove = GetDecimalValue(arguments, MinimumMoveArgument);
            if (minimumMove < 0m)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{MinimumMoveArgument}' must be 0 or more.", MinimumMoveArgument);
            }
        }

        decimal tolerance = ChartConstants.ThreeLineBreakTrendThreshold;
        if (HasProperty(arguments, ToleranceArgument))
        {
            tolerance = GetDecimalValue(arguments, ToleranceArgument);
            if (tolerance < 0m)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{ToleranceArgument}' must be 0 or more.", ToleranceArgument);
            }
        }

        // 8. Pattern toggles
        if (HasProperty(arguments, IncludeMultiWaveArgument) && chartType == ChartType.HeikinAshi)
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{IncludeMultiWaveArgument}' is not applicable to HeikinAshi charts.", IncludeMultiWaveArgument);

        bool includeMultiWave = ToolArgs.Boolean(arguments, IncludeMultiWaveArgument) ?? false;

        string[] pnfFlags = { IncludePatternsArgument, ShowDoubleBreakoutArgument, ShowTripleBreakoutArgument, ShowTrendlineBreakoutArgument, ShowTriangleBreakoutArgument, ShowCatapultBreakoutArgument };
        foreach (var flag in pnfFlags)
        {
            if (HasProperty(arguments, flag) && chartType != ChartType.PointAndFigure)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{flag}' is only applicable to PointAndFigure charts.", flag);
            }
        }

        bool includePatterns = ToolArgs.Boolean(arguments, IncludePatternsArgument) ?? false;
        bool showDouble = ToolArgs.Boolean(arguments, ShowDoubleBreakoutArgument) ?? true;
        bool showTriple = ToolArgs.Boolean(arguments, ShowTripleBreakoutArgument) ?? true;
        bool showTrendline = ToolArgs.Boolean(arguments, ShowTrendlineBreakoutArgument) ?? true;
        bool showTriangle = ToolArgs.Boolean(arguments, ShowTriangleBreakoutArgument) ?? true;
        bool showCatapult = ToolArgs.Boolean(arguments, ShowCatapultBreakoutArgument) ?? true;

        return new ChartSeriesRequest(
            ChartType: chartType,
            SizingMode: sizingMode,
            ManualSize: manualSize,
            Percentage: percentage,
            AtrPeriod: atrPeriod,
            AtrMultiplier: atrMultiplier,
            RoundingMode: roundingMode,
            FallbackMode: fallbackMode,
            Reversal: reversal,
            InitialColumn: initialColumn,
            LineCount: lineCount,
            MinimumMove: minimumMove,
            Tolerance: tolerance,
            IncludeMultiWave: includeMultiWave,
            IncludePatterns: includePatterns,
            ShowDoubleBreakout: showDouble,
            ShowTripleBreakout: showTriple,
            ShowTrendlineBreakout: showTrendline,
            ShowTriangleBreakout: showTriangle,
            ShowCatapultBreakout: showCatapult);
    }

    private static bool HasProperty(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out _);

    private static decimal GetDecimalValue(JsonElement arguments, string name)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out decimal val))
            {
                return val;
            }
        }

        throw new ToolException(ToolErrorCodes.InvalidArgument, $"Argument '{name}' must be a valid number.", name);
    }

    private static void WriteResult(
        Utf8JsonWriter writer,
        string symbol,
        TimeFrame timeFrame,
        ChartType chartType,
        int sourceBarCount,
        ChartSeriesCalculationResult result,
        int tail,
        DateOnly? endDate)
    {
        writer.WriteStartObject();
        writer.WriteString("symbol", symbol);
        writer.WriteString("timeFrame", timeFrame.ToString());
        writer.WriteString("chartType", chartType.ToString());
        if (endDate is not null)
        {
            writer.WriteString("endDate", endDate.Value.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
        }

        int takeCount = Math.Min(result.Elements.Count, tail);
        int skipCount = Math.Max(0, result.Elements.Count - takeCount);

        writer.WriteNumber("sourceBarCount", sourceBarCount);
        writer.WriteNumber("elementCount", result.ElementCount);
        writer.WriteNumber("outputStartElementIndex", skipCount);

        if (result.EffectiveSize is not null)
        {
            writer.WriteNumber("effectiveSize", result.EffectiveSize.Value);
        }

        if (result.TotalColumns is not null)
        {
            writer.WriteNumber("totalColumns", result.TotalColumns.Value);
        }

        writer.WriteStartArray("elements");

        for (int i = skipCount; i < result.Elements.Count; i++)
        {
            var el = result.Elements[i];
            writer.WriteStartObject();
            writer.WriteNumber("index", el.Index);
            writer.WriteString("timestamp", ToolJson.FormatDate(el.Timestamp));
            writer.WriteNumber("open", el.Open);
            writer.WriteNumber("high", el.High);
            writer.WriteNumber("low", el.Low);
            writer.WriteNumber("close", el.Close);
            writer.WriteNumber("volume", el.Volume);
            writer.WriteString("direction", el.Direction);

            if (el.ColumnIndex is not null) writer.WriteNumber("columnIndex", el.ColumnIndex.Value);
            if (el.ColumnType is not null) writer.WriteString("columnType", el.ColumnType);
            if (el.IsYang is not null) writer.WriteBoolean("isYang", el.IsYang.Value);
            if (el.LineState is not null) writer.WriteString("lineState", el.LineState);
            if (el.ColumnSpan is not null) writer.WriteNumber("columnSpan", el.ColumnSpan.Value);
            if (el.RunCount is not null) writer.WriteNumber("runCount", el.RunCount.Value);
            if (el.BoxCount is not null) writer.WriteNumber("boxCount", el.BoxCount.Value);
            if (el.ReversalPrice is not null) writer.WriteNumber("reversalPrice", el.ReversalPrice.Value);
            if (el.NextTargetPrice is not null) writer.WriteNumber("nextTargetPrice", el.NextTargetPrice.Value);
            if (el.StartDate is not null) writer.WriteString("startDate", ToolJson.FormatDate(el.StartDate.Value));
            if (el.EndDate is not null) writer.WriteString("endDate", ToolJson.FormatDate(el.EndDate.Value));
            if (el.ReversalDate is not null) writer.WriteString("reversalDate", ToolJson.FormatDate(el.ReversalDate.Value));
            if (el.UpdateDate is not null) writer.WriteString("updateDate", ToolJson.FormatDate(el.UpdateDate.Value));
            if (el.TrendDays is not null) writer.WriteNumber("trendDays", el.TrendDays.Value);

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        if (result.MultiWaveSignals is not null)
        {
            writer.WriteStartArray("multiWaveSignals");
            foreach (var sig in result.MultiWaveSignals)
            {
                writer.WriteStartObject();
                writer.WriteString("patternType", sig.PatternType.ToString());
                writer.WriteBoolean("isBullish", sig.IsBullish);
                writer.WriteNumber("triggerIndex", sig.TriggerIndex);
                writer.WriteNumber("triggerPrice", sig.TriggerPrice);
                writer.WriteNumber("signalLevel", sig.SignalLevel);
                writer.WriteNumber("confidenceScore", sig.ConfidenceScore);
                writer.WriteNumber("invalidationPrice", sig.InvalidationPrice);
                writer.WriteNumber("targetPriceMin", sig.TargetPriceMin);
                writer.WriteNumber("targetPriceMax", sig.TargetPriceMax);
                writer.WriteBoolean("isInvalidated", sig.IsInvalidated);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        if (result.PnfAnalysis is not null)
        {
            writer.WriteStartObject("pnfAnalysis");
            writer.WriteStartArray("signals");
            foreach (var sig in result.PnfAnalysis.Value.Signals)
            {
                writer.WriteStartObject();
                writer.WriteString("patternType", sig.PatternType.ToString());
                writer.WriteBoolean("isBullish", sig.IsBullish);
                writer.WriteString("triggerTimestamp", ToolJson.FormatDate(sig.TriggerTimestamp));
                writer.WriteNumber("triggerPrice", sig.TriggerPrice);
                writer.WriteNumber("signalLevel", sig.SignalLevel);
                writer.WriteNumber("anchorColOffset", sig.AnchorColOffset);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("activeTrendlines");
            foreach (var line in result.PnfAnalysis.Value.ActiveLines)
            {
                writer.WriteStartObject();
                writer.WriteNumber("anchorIndex", line.AnchorIndex);
                writer.WriteNumber("anchorPrice", line.AnchorPrice);
                writer.WriteBoolean("isBullish", line.IsBullish);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}
