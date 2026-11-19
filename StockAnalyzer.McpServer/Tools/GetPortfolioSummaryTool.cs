using System.Text.Json;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;
using static StockAnalyzer.McpServer.Tools.PortfolioToolSupport;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_get_portfolio_summary: the saved portfolio valued at the latest stored prices in one base currency, by the application's own valuation (<c>PortfolioManager.Evaluate</c>).
/// Read-only: the files are read as they are, nothing is recovered, created or written.
/// </summary>
internal sealed class GetPortfolioSummaryTool : IMcpTool
{
    private const string IncludeClosedPositionsArgument = "includeClosedPositions";
    private const string IncludeTransactionsArgument = "includeTransactions";
    private const string LimitArgument = "limit";

    private readonly IReadOnlyPortfolioSource _source;
    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly McpLimits _limits;

    public GetPortfolioSummaryTool(IReadOnlyPortfolioSource source, IReadOnlyStockAnalyzerGateway gateway, McpLimits limits)
    {
        _source = source;
        _gateway = gateway;
        _limits = limits;
    }

    public string Name => "sa_get_portfolio_summary";

    public string Description =>
        $"Returns the saved portfolio's valuation, cash balances, open positions in {BaseCurrencyArgument} (default JPY), exchange rates, and optional closedPositions/transactions history (limit default {_limits.MaxReturnedClosedPositions}/{_limits.MaxReturnedTransactions}). Read-only. " +
        ToolJson.DocLink(Name);

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"baseCurrency\":\"JPY\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(BaseCurrencyArgument, "ISO 4217 code of the currency every total is shown in. Default JPY.")
        .Boolean(IncludeClosedPositionsArgument, "true adds closedPositions: the closed trades, newest exit first. Default false.")
        .Boolean(IncludeTransactionsArgument, "true adds transactions: the saved transaction history, newest first (notes, target price and stop loss are never returned). Default false.")
        .Integer(LimitArgument, $"Most items per requested list (needs includeClosedPositions or includeTransactions). Default and maximum {_limits.MaxReturnedClosedPositions} closed positions and {_limits.MaxReturnedTransactions} transactions.", minimum: 1, maximum: Math.Max(_limits.MaxReturnedClosedPositions, _limits.MaxReturnedTransactions))
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        CurrencyCode baseCurrency = ReadBaseCurrency(arguments);
        (int? closedLimit, int? transactionLimit) = ReadHistoryRequest(arguments);
        PortfolioReadResult read = await LoadAsync(_source, cancellationToken).ConfigureAwait(false);
        Portfolio portfolio = read.Portfolio;
        Dictionary<string, LatestPrice> latest = await LatestPricesAsync(_gateway, portfolio.Positions.Values.Select(p => p.Ticker), cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<CurrencyCode, ExchangeRate> rates = PortfolioExchangeRates.ExtractLatest(portfolio.History);
        PortfolioEvaluationResult evaluation = new PortfolioManager().Evaluate(
            portfolio, latest.ToDictionary(p => p.Key, p => p.Value.Close, StringComparer.OrdinalIgnoreCase), rates, baseCurrency);

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("portfolioFound", read.Found);
            writer.WriteString(BaseCurrencyArgument, baseCurrency.Value);
            writer.WriteStartObject("metrics");
            ToolJson.WriteNullableNumber(writer, "totalValue", evaluation.Metrics.TotalValue);
            ToolJson.WriteNullableNumber(writer, "totalUnrealizedPL", evaluation.Metrics.TotalUnrealizedPL);
            ToolJson.WriteNullableNumber(writer, "totalRealizedPL", evaluation.Metrics.TotalRealizedPL);
            ToolJson.WriteNullableNumber(writer, "cashBalance", evaluation.Metrics.CashBalance);
            ToolJson.WriteNullableNumber(writer, "cashRatioPercent", evaluation.Metrics.CashRatio);
            writer.WriteEndObject();

            writer.WriteStartArray("cashBalances");
            foreach ((CurrencyCode currency, decimal amount) in portfolio.CashBalances.OrderBy(c => c.Key.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("currency", currency.Value);
                writer.WriteNumber("amount", amount);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("positions");
            foreach ((string key, Position position) in portfolio.Positions.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                bool priced = latest.TryGetValue(position.Ticker, out LatestPrice price);
                bool converted = evaluation.PositionValues.TryGetValue(key, out decimal valueInBase);
                writer.WriteStartObject();
                writer.WriteString("ticker", position.Ticker);
                writer.WriteBoolean("isShort", position.IsShort);
                writer.WriteNumber("quantity", position.Quantity);
                writer.WriteNumber("averageCostPerUnit", position.AverageCostPerUnit);
                writer.WriteString("costCurrency", position.AverageCost.Currency.Value);
                writer.WriteBoolean("priceAvailable", priced);
                ToolJson.WriteNullableNumber(writer, "latestPrice", priced ? price.Close : null);
                if (priced)
                {
                    writer.WriteString("latestPriceDate", ToolJson.FormatDate(price.Date));
                }

                writer.WriteBoolean("convertedToBase", converted);
                ToolJson.WriteNullableNumber(writer, "marketValueInBase", converted ? valueInBase : null);
                ToolJson.WriteNullableNumber(writer, "unrealizedPLInBase", converted && evaluation.PositionPLs.TryGetValue(key, out decimal pl) ? pl : null);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteNumber("closedPositionCount", portfolio.ClosedPositions.Count);
            writer.WriteStartArray("realizedPLByCurrency");
            foreach ((CurrencyCode currency, decimal amount) in (portfolio.RealizedPnLByCurrency ?? new Dictionary<CurrencyCode, decimal>()).OrderBy(c => c.Key.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("currency", currency.Value);
                writer.WriteNumber("amount", amount);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("unconvertedCurrencies");
            foreach (CurrencyCode currency in evaluation.UnconvertedCurrencies ?? Array.Empty<CurrencyCode>())
            {
                writer.WriteStringValue(currency.Value);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("exchangeRates");
            foreach (ExchangeRate rate in rates.Values.OrderBy(r => r.BaseCurrency.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("baseCurrency", rate.BaseCurrency.Value);
                writer.WriteString("quoteCurrency", rate.QuoteCurrency.Value);
                writer.WriteNumber("rate", rate.Rate);
                writer.WriteString("asOf", ToolJson.FormatDate(rate.AsOf));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (closedLimit is { } closed)
            {
                PortfolioHistoryJson.WriteClosedPositions(writer, portfolio.ClosedPositions, closed);
            }

            if (transactionLimit is { } transactions)
            {
                PortfolioHistoryJson.WriteTransactions(writer, portfolio.History, transactions);
            }

            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Reads <c>includeClosedPositions</c>, <c>includeTransactions</c> and <c>limit</c> before anything is loaded. Each is the number of items its list returns, or null when the list was not asked for.
    /// <c>limit</c> must be 1 or more (INVALID_ARGUMENT) and no larger than the limit of every list asked for (LIMIT_EXCEEDED); without a list it has nothing to limit (INVALID_ARGUMENT).
    /// </summary>
    private (int? ClosedPositions, int? Transactions) ReadHistoryRequest(JsonElement arguments)
    {
        bool closed = ToolArgs.Boolean(arguments, IncludeClosedPositionsArgument) ?? false;
        bool transactions = ToolArgs.Boolean(arguments, IncludeTransactionsArgument) ?? false;
        if (ToolArgs.Integer(arguments, LimitArgument) is null)
        {
            return (closed ? _limits.MaxReturnedClosedPositions : null, transactions ? _limits.MaxReturnedTransactions : null);
        }

        if (!closed && !transactions)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{LimitArgument}' limits the lists of {IncludeClosedPositionsArgument} and {IncludeTransactionsArgument} and has no effect without one of them.",
                LimitArgument,
                $"Set {IncludeClosedPositionsArgument} or {IncludeTransactionsArgument} to true, or omit {LimitArgument}.");
        }

        int largest = Math.Min(closed ? _limits.MaxReturnedClosedPositions : int.MaxValue, transactions ? _limits.MaxReturnedTransactions : int.MaxValue);
        int limit = ToolArgs.PositiveWithin(arguments, LimitArgument, largest, largest);
        return (closed ? limit : null, transactions ? limit : null);
    }
}
