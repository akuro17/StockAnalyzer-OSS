using System.Text.Json;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using static StockAnalyzer.McpServer.Tools.PortfolioToolSupport;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// sa_get_portfolio_summary: the saved portfolio valued at the latest stored prices in one base currency, by the application's own valuation (<c>PortfolioManager.Evaluate</c>).
/// Read-only: the files are read as they are, nothing is recovered, created or written.
/// </summary>
internal sealed class GetPortfolioSummaryTool : IMcpTool
{
    private readonly IReadOnlyPortfolioSource _source;
    private readonly IReadOnlyStockAnalyzerGateway _gateway;

    public GetPortfolioSummaryTool(IReadOnlyPortfolioSource source, IReadOnlyStockAnalyzerGateway gateway)
    {
        _source = source;
        _gateway = gateway;
    }

    public string Name => "sa_get_portfolio_summary";

    public string Description =>
        "Returns the user's saved portfolio valued by the application's own valuation: total value, unrealized and realized profit, cash and its ratio, every open position with its latest stored price, " +
        $"market value and unrealized profit in the base currency ({BaseCurrencyArgument}, an ISO 4217 code, default JPY), the cash by currency and the exchange rates used. " +
        "The exchange rates are the ones the user's own transactions applied (the newest per currency and their inverses); nothing is fetched. A currency without a rate to the base currency is listed in unconvertedCurrencies " +
        "and its positions are left out of the base-currency totals (a portfolio that holds a single currency is valued in it unchanged). A position without a stored price is valued at its average cost with no unrealized profit (priceAvailable false). " +
        "Short positions have a negative market value. Read-only; the file is read as it is. No portfolio saved: portfolioFound false and empty lists.";

    public bool IsHeavy => false;

    public string? ExampleArgumentsJson => "{\"baseCurrency\":\"JPY\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .String(BaseCurrencyArgument, "ISO 4217 code of the currency every total is shown in. Default JPY.")
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        CurrencyCode baseCurrency = ReadBaseCurrency(arguments);
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
            writer.WriteEndObject();
        });
    }
}
