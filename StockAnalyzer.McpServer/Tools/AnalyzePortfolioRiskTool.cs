using System.Text.Json;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;
using static StockAnalyzer.McpServer.Tools.PortfolioToolSupport;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_analyze_portfolio_risk: historical value at risk, conditional value at risk, volatility and the contribution of each holding, for the saved portfolio weighted by its market values
/// in one base currency (owner decisions U-06 and U-07). The valuation is the application's own (<c>PortfolioManager.Evaluate</c>); the risk figures are <see cref="PortfolioRiskCalculator"/>.
/// </summary>
internal sealed class AnalyzePortfolioRiskTool : IMcpTool
{
    private const string LookbackArgument = "lookbackBars";
    private const string ConfidenceArgument = "confidenceLevel";
    private const string BenchmarkArgument = "benchmark";

    /// <summary>The confidence level when the call does not say; a conventional 95 % historical risk figure.</summary>
    private const double DefaultConfidenceLevel = 0.95;

    private readonly IReadOnlyPortfolioSource _source;
    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly SymbolResolver _symbols;
    private readonly McpLimits _limits;
    private readonly CandleWindowLoader _loader;

    public AnalyzePortfolioRiskTool(IReadOnlyPortfolioSource source, IReadOnlyStockAnalyzerGateway gateway, SymbolResolver symbols, McpLimits limits)
    {
        _source = source;
        _gateway = gateway;
        _symbols = symbols;
        _limits = limits;
        _loader = new CandleWindowLoader(gateway);
    }

    public string Name => "sa_analyze_portfolio_risk";

    public string Description =>
        $"Historical one-bar daily portfolio risk (VaR/CVaR as positive loss ratios at confidenceLevel, volatility, marginal/component risk, optional benchmark beta) in {BaseCurrencyArgument} (default JPY); supports {SeriesOutputOptions.DecimalPlacesArgument}. Read-only. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"baseCurrency\":\"JPY\",\"lookbackBars\":250,\"confidenceLevel\":0.95}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(BaseCurrencyArgument, "ISO 4217 code of the currency the exposures are measured in. Default JPY.")
        .Integer(LookbackArgument, $"Daily bars the returns are taken from, newest last. Default {_limits.DefaultCandleCount}, maximum {_limits.MaxCandlesPerRequest}.", defaultValue: _limits.DefaultCandleCount, minimum: 1, maximum: _limits.MaxCandlesPerRequest)
        .Number(ConfidenceArgument, $"Confidence level of the value at risk, from {PortfolioRiskCalculator.MinConfidenceLevel} to {PortfolioRiskCalculator.MaxConfidenceLevel}. Default {DefaultConfidenceLevel}.")
        .String(BenchmarkArgument, "Optional benchmark symbol; adds the portfolio beta.")
        .Integer(SeriesOutputOptions.DecimalPlacesArgument, SeriesOutputOptions.DecimalPlacesDescription("every exposure, risk figure, weight and volatility (confidenceLevel is echoed as sent)"), minimum: 0, maximum: SeriesOutputOptions.MaxDecimalPlaces)
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        CurrencyCode baseCurrency = ReadBaseCurrency(arguments);
        int lookback = ToolArgs.PositiveWithin(arguments, LookbackArgument, _limits.DefaultCandleCount, _limits.MaxCandlesPerRequest);
        double confidence = ToolArgs.Number(arguments, ConfidenceArgument) ?? DefaultConfidenceLevel;
        if (!(confidence >= PortfolioRiskCalculator.MinConfidenceLevel && confidence <= PortfolioRiskCalculator.MaxConfidenceLevel))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{ConfidenceArgument}' must be from {PortfolioRiskCalculator.MinConfidenceLevel} to {PortfolioRiskCalculator.MaxConfidenceLevel}.",
                ConfidenceArgument);
        }

        int? decimalPlaces = SeriesOutputOptions.ReadDecimalPlaces(arguments);
        string? benchmark = null;
        if (ToolArgs.String(arguments, BenchmarkArgument) is { } benchmarkText)
        {
            benchmark = await _symbols.ResolveAsync(benchmarkText, BenchmarkArgument, cancellationToken).ConfigureAwait(false);
        }

        PortfolioReadResult read = await LoadAsync(_source, cancellationToken).ConfigureAwait(false);
        Portfolio portfolio = read.Portfolio;
        Dictionary<string, LatestPrice> latest = await LatestPricesAsync(_gateway, portfolio.Positions.Values.Select(p => p.Ticker), cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<CurrencyCode, ExchangeRate> rates = PortfolioExchangeRates.ExtractLatest(portfolio.History);
        PortfolioEvaluationResult evaluation = new PortfolioManager().Evaluate(
            portfolio, latest.ToDictionary(p => p.Key, p => p.Value.Close, StringComparer.OrdinalIgnoreCase), rates, baseCurrency);

        var exposures = new List<PortfolioExposure>();
        var excluded = new List<ExcludedSymbolInfo>();
        var candles = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, Position position) in portfolio.Positions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!latest.ContainsKey(position.Ticker))
            {
                excluded.Add(new ExcludedSymbolInfo(position.Ticker, "No stored price"));
                continue;
            }

            if (!evaluation.PositionValues.TryGetValue(key, out decimal value))
            {
                excluded.Add(new ExcludedSymbolInfo(position.Ticker, "No exchange rate to base currency"));
                continue;
            }

            exposures.Add(new PortfolioExposure(position.Ticker, value));
            if (!candles.ContainsKey(position.Ticker))
            {
                (List<CandleData> bars, _) = await _loader.LoadAsync(position.Ticker, TimeFrame.D1, lookback, cancellationToken).ConfigureAwait(false);
                candles[position.Ticker] = bars;
            }
        }

        IReadOnlyList<CandleData>? benchmarkCandles = null;
        if (benchmark is not null && !candles.ContainsKey(benchmark))
        {
            (List<CandleData> bars, _) = await _loader.LoadAsync(benchmark, TimeFrame.D1, lookback, cancellationToken).ConfigureAwait(false);
            benchmarkCandles = bars;
        }

        PortfolioRiskResult risk = PortfolioRiskCalculator.Calculate(exposures, candles, confidence, benchmark, benchmarkCandles);

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("portfolioFound", read.Found);
            writer.WriteString(BaseCurrencyArgument, baseCurrency.Value);
            writer.WriteString("periodUnit", GetCapabilitiesTool.PeriodUnit);
            writer.WriteNumber(LookbackArgument, lookback);
            writer.WriteNumber(ConfidenceArgument, confidence);
            if (benchmark is not null)
            {
                writer.WriteString(BenchmarkArgument, benchmark);
            }

            ToolJson.WriteNullableNumber(writer, "grossExposure", SeriesOutputOptions.RoundIfRequested(risk.GrossExposure, decimalPlaces));
            ToolJson.WriteNullableNumber(writer, "netExposure", SeriesOutputOptions.RoundIfRequested(risk.NetExposure, decimalPlaces));
            writer.WriteNumber("commonDateCount", risk.CommonDateCount);
            if (risk.StartDate is { } start)
            {
                writer.WriteString("startDate", ToolJson.FormatDate(start));
            }

            if (risk.EndDate is { } end)
            {
                writer.WriteString("endDate", ToolJson.FormatDate(end));
            }

            ToolJson.WriteNullableNumber(writer, "valueAtRisk", SeriesOutputOptions.RoundIfRequested(risk.ValueAtRisk, decimalPlaces));
            ToolJson.WriteNullableNumber(writer, "conditionalValueAtRisk", SeriesOutputOptions.RoundIfRequested(risk.ConditionalValueAtRisk, decimalPlaces));
            ToolJson.WriteNullableNumber(writer, "portfolioVolatility", SeriesOutputOptions.RoundIfRequested(risk.PortfolioVolatility, decimalPlaces));
            if (benchmark is not null)
            {
                ToolJson.WriteNullableNumber(writer, "beta", SeriesOutputOptions.RoundIfRequested(risk.Beta, decimalPlaces));
            }

            writer.WriteStartArray("contributions");
            foreach (PortfolioRiskContribution contribution in risk.Contributions)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", contribution.Symbol);
                writer.WriteNumber("signedValueInBase", SeriesOutputOptions.RoundIfRequested(contribution.SignedValue, decimalPlaces));
                writer.WriteNumber("weight", SeriesOutputOptions.RoundIfRequested(contribution.Weight, decimalPlaces));
                ToolJson.WriteNullableNumber(writer, "marginalVolatility", SeriesOutputOptions.RoundIfRequested(contribution.MarginalVolatility, decimalPlaces));
                ToolJson.WriteNullableNumber(writer, "componentVolatility", SeriesOutputOptions.RoundIfRequested(contribution.ComponentVolatility, decimalPlaces));
                ToolJson.WriteNullableNumber(writer, "tailComponentCVaR", SeriesOutputOptions.RoundIfRequested(contribution.TailComponentCVaR, decimalPlaces));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("excludedSymbols");
            foreach (ExcludedSymbolInfo info in excluded.Concat(risk.ExcludedSymbols))
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", info.Symbol);
                writer.WriteString("reason", info.Reason);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("unconvertedCurrencies");
            foreach (CurrencyCode currency in evaluation.UnconvertedCurrencies ?? Array.Empty<CurrencyCode>())
            {
                writer.WriteStringValue(currency.Value);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("definitions");
            foreach (string definition in Definitions)
            {
                writer.WriteStringValue(definition);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("notes");
            foreach (string note in risk.Notes)
            {
                writer.WriteStringValue(note);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static readonly string[] Definitions =
    {
        "weight = signed market value in the base currency / gross exposure (sum of absolute values); a symbol held long and short is netted",
        "portfolio return per bar = sum of weight x simple return of the symbol, on the dates every held symbol has a return",
        "valueAtRisk = -(return at index k of the ascending-sorted portfolio returns), k = clamp(floor((1 - confidenceLevel) x commonDateCount), 0, commonDateCount - 1) (tailIndex)",
        "conditionalValueAtRisk = -(mean of the k + 1 worst portfolio returns); the tailComponentCVaR values sum to it",
        "portfolioVolatility = sqrt(w' S w) with S the sample covariance (N-1) of the symbols' returns, per bar, not annualized; componentVolatility = weight x marginalVolatility, marginalVolatility = (S w) / portfolioVolatility",
    };
}
