using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_project_price_path: generates multi-step mathematical price projection trajectories
/// (ARIMA, FFT, SSA, HMM) with confidence intervals in a strictly causal, prefix-invariant manner.
/// </summary>
internal sealed class ProjectPricePathTool : IMcpTool
{
    private const string SymbolArgument = "symbol";
    private const string TimeFrameArgument = "timeFrame";
    private const string EndDateArgument = "endDate";
    private const string LookbackArgument = "lookbackBars";
    private const string MethodArgument = "method";
    private const string HorizonBarsArgument = "horizonBars";
    private const string ConfidenceMultiplierArgument = "confidenceMultiplier";
    private const string ArimaPArgument = "arimaP";
    private const string ArimaDArgument = "arimaD";
    private const string ArimaQArgument = "arimaQ";
    private const string FftHarmonicsArgument = "fftHarmonics";
    private const string SsaEmbeddingDimensionArgument = "ssaEmbeddingDimension";
    private const string SsaNumComponentsArgument = "ssaNumComponents";
    private const string HmmStatesArgument = "hmmStates";

    private const int DefaultHorizonBars = 20;
    private const decimal DefaultConfidenceMultiplier = 2.0m;

    private static readonly string[] AllowedMethods = { "Arima", "Fft", "Ssa", "Hmm" };

    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly CandleWindowLoader _loader;
    private readonly McpLimits _limits;

    public ProjectPricePathTool(IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _loader = new CandleWindowLoader(gateway);
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public string Name => "sa_project_price_path";

    public string Description =>
        $"Generates multi-step mathematical price projection trajectories (ARIMA, FFT, SSA, HMM) with confidence intervals. " +
        $"{McpDisclaimerConstants.ResearchNotAdvice} Strictly causal and prefix-invariant: projections never depend on future bars.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson =>
        "{\"symbol\":\"7203-T\",\"timeFrame\":\"D1\",\"method\":\"Arima\",\"horizonBars\":20}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(SymbolArgument, "Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same).", required: true)
        .String(TimeFrameArgument, $"Time frame of the bars ({ToolArgs.SupportedTimeFrameNames}).", required: true, allowedValues: Enum.GetNames<TimeFrame>())
        .String(EndDateArgument, $"Last date to include ({ToolJson.DateFormat}). If specified, bars up to this date are evaluated.")
        .Integer(LookbackArgument, $"Stored bars loaded up to endDate, newest last. Default and maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.MaxCandlesPerRequest, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .String(MethodArgument, "Projection extrapolation method (Arima, Fft, Ssa, Hmm).", required: true, allowedValues: AllowedMethods)
        .Integer(HorizonBarsArgument, $"Number of forward bars to project (1..{_limits.MaxProjectionBars}). Default {DefaultHorizonBars}.", defaultValue: DefaultHorizonBars, minimum: 1, maximum: _limits.MaxProjectionBars)
        .Number(ConfidenceMultiplierArgument, $"Confidence band standard deviation multiplier (0.0..5.0). Default {DefaultConfidenceMultiplier}.")
        .Integer(ArimaPArgument, "ARIMA autoregressive order p (0..5). Default 1.", defaultValue: 1, minimum: 0, maximum: 5)
        .Integer(ArimaDArgument, "ARIMA differencing degree d (0..2). Default 1.", defaultValue: 1, minimum: 0, maximum: 2)
        .Integer(ArimaQArgument, "ARIMA moving-average order q (0..5). Default 1.", defaultValue: 1, minimum: 0, maximum: 5)
        .Integer(FftHarmonicsArgument, "FFT number of dominant harmonic components (1..10). Default 3.", defaultValue: 3, minimum: 1, maximum: 10)
        .Integer(SsaEmbeddingDimensionArgument, $"SSA lag window embedding dimension (2..100). Default {IndicatorDefaultConstants.SsaDefaultEmbeddingDimension}.", defaultValue: IndicatorDefaultConstants.SsaDefaultEmbeddingDimension, minimum: 2, maximum: 100)
        .Integer(SsaNumComponentsArgument, $"SSA number of principal reconstructed components (1..20). Default {IndicatorDefaultConstants.SsaDefaultNumComponents}.", defaultValue: IndicatorDefaultConstants.SsaDefaultNumComponents, minimum: 1, maximum: 20)
        .Integer(HmmStatesArgument, $"HMM hidden state count (2..3). Default {IndicatorDefaultConstants.HmmStates}.", defaultValue: IndicatorDefaultConstants.HmmStates, minimum: 2, maximum: 3)
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
        int horizon = ToolArgs.PositiveWithin(arguments, HorizonBarsArgument, DefaultHorizonBars, _limits.MaxProjectionBars);

        string rawMethod = ToolArgs.String(arguments, MethodArgument)
            ?? throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{MethodArgument}' is required.", MethodArgument);
        string canonicalMethod = AllowedMethods.FirstOrDefault(m => string.Equals(m, rawMethod, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException(ToolErrorCodes.InvalidArgument, $"Unknown method '{rawMethod}'. Allowed methods are: {string.Join(", ", AllowedMethods)}.", MethodArgument);

        double? rawMult = ToolArgs.Number(arguments, ConfidenceMultiplierArgument);
        if (rawMult.HasValue && (rawMult.Value < 0.0 || rawMult.Value > 5.0 || double.IsNaN(rawMult.Value) || double.IsInfinity(rawMult.Value)))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{ConfidenceMultiplierArgument} must be between 0.0 and 5.0.", ConfidenceMultiplierArgument);
        }
        decimal confidenceMultiplier = rawMult.HasValue ? (decimal)rawMult.Value : DefaultConfidenceMultiplier;

        int arimaP = ParseIntWithinRange(arguments, ArimaPArgument, 1, 0, 5);
        int arimaD = ParseIntWithinRange(arguments, ArimaDArgument, 1, 0, 2);
        int arimaQ = ParseIntWithinRange(arguments, ArimaQArgument, 1, 0, 5);
        int fftHarmonics = ParseIntWithinRange(arguments, FftHarmonicsArgument, 3, 1, 10);
        int ssaDim = ParseIntWithinRange(arguments, SsaEmbeddingDimensionArgument, IndicatorDefaultConstants.SsaDefaultEmbeddingDimension, 2, 100);
        int ssaComp = ParseIntWithinRange(arguments, SsaNumComponentsArgument, IndicatorDefaultConstants.SsaDefaultNumComponents, 1, 20);
        int hmmStates = ParseIntWithinRange(arguments, HmmStatesArgument, IndicatorDefaultConstants.HmmStates, 2, 3);

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

        var coreCandles = loaded.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
        TimeSpan timeframeSpan = timeFrame switch
        {
            TimeFrame.D1 => TimeSpan.FromDays(1),
            TimeFrame.W1 => TimeSpan.FromDays(7),
            TimeFrame.MN1 => TimeSpan.FromDays(30),
            _ => TimeSpan.FromDays(1)
        };

        IReadOnlyList<Point> projectedPoints;
        IReadOnlyList<Point> upperBandPoints;
        IReadOnlyList<Point> lowerBandPoints;

        int startIndex = 0;
        int endIndex = coreCandles.Count - 1;

        double[] samples = loaded.Select(c => (double)c.Close).ToArray();
        DateTime[] timestamps = loaded.Select(c => c.Timestamp).ToArray();

        switch (canonicalMethod)
        {
            case "Arima":
                ArimaProjectionResult arimaRes = ArimaProjectionAnalysis.CalculateProjection(
                    coreCandles, startIndex, endIndex,
                    p: arimaP, d: arimaD, q: arimaQ,
                    futureSteps: horizon,
                    priceSource: PriceType.Close,
                    timeframeSpan: timeframeSpan,
                    showConfidenceBand: true,
                    confidenceMultiplier: confidenceMultiplier);
                projectedPoints = arimaRes.ProjectedPoints;
                upperBandPoints = arimaRes.UpperBandPoints;
                lowerBandPoints = arimaRes.LowerBandPoints;
                break;

            case "Fft":
                FftProjectionResult fftRes = FftProjectionAnalysis.CalculateProjection(
                    samples,
                    timestamps,
                    futureSteps: horizon,
                    timeframeSpan: timeframeSpan,
                    harmonicCount: fftHarmonics,
                    showConfidenceBand: true,
                    confidenceMultiplier: confidenceMultiplier);
                projectedPoints = fftRes.ProjectedPoints;
                upperBandPoints = fftRes.UpperBandPoints;
                lowerBandPoints = fftRes.LowerBandPoints;
                break;

            case "Ssa":
                SsaProjectionResult ssaRes = SsaProjectionAnalysis.CalculateProjection(
                    samples,
                    timestamps,
                    futureSteps: horizon,
                    timeframeSpan: timeframeSpan,
                    embeddingDimension: ssaDim,
                    numComponents: ssaComp,
                    detrendMode: SsaDetrendMode.LeastSquaresLinear,
                    showConfidenceBand: true,
                    confidenceMultiplier: confidenceMultiplier,
                    forecastMode: SsaForecastMode.Recurrent);
                projectedPoints = ssaRes.ProjectedPoints;
                upperBandPoints = ssaRes.UpperBandPoints;
                lowerBandPoints = ssaRes.LowerBandPoints;
                break;

            case "Hmm":
                HmmProjectionResult hmmRes = HmmProjectionAnalysis.CalculateProjection(
                    samples,
                    timestamps,
                    futureSteps: horizon,
                    timeframeSpan: timeframeSpan,
                    states: hmmStates,
                    showConfidenceBand: true,
                    confidenceMultiplier: confidenceMultiplier);
                projectedPoints = hmmRes.ProjectedPoints;
                upperBandPoints = hmmRes.UpperBandPoints;
                lowerBandPoints = hmmRes.LowerBandPoints;
                break;

            default:
                projectedPoints = Array.Empty<Point>();
                upperBandPoints = Array.Empty<Point>();
                lowerBandPoints = Array.Empty<Point>();
                break;
        }

        bool isEmpty = projectedPoints.Count <= 1;

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", symbol);
            writer.WriteString("timeFrame", timeFrame.ToString());
            writer.WriteString("asOfDate", ToolJson.FormatDate(loaded[^1].Timestamp));
            writer.WriteString("method", canonicalMethod);
            writer.WriteNumber("horizonBars", horizon);
            writer.WriteString("disclaimer", McpDisclaimerConstants.ResearchNotAdvice);
            writer.WriteBoolean("isEmpty", isEmpty);

            writer.WriteStartArray("projectedPath");
            if (!isEmpty)
            {
                // Index 0 is the anchor candle (h=0); indices 1..N are future projected steps (h=1..N)
                for (int k = 1; k < projectedPoints.Count; k++)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("barOffset", k);
                    writer.WriteString("date", ToolJson.FormatDate(new DateTime((long)projectedPoints[k].X)));
                    writer.WriteNumber("value", projectedPoints[k].Y);

                    if (k < lowerBandPoints.Count)
                    {
                        writer.WriteNumber("lower", lowerBandPoints[k].Y);
                    }
                    if (k < upperBandPoints.Count)
                    {
                        writer.WriteNumber("upper", upperBandPoints[k].Y);
                    }

                    writer.WriteEndObject();
                }
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

    private static int ParseIntWithinRange(JsonElement arguments, string name, int defaultValue, int min, int max)
    {
        long? raw = ToolArgs.Integer(arguments, name);
        if (!raw.HasValue)
        {
            return defaultValue;
        }

        if (raw.Value < min || raw.Value > max)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"{name} must be between {min} and {max}.", name);
        }

        return (int)raw.Value;
    }
}
