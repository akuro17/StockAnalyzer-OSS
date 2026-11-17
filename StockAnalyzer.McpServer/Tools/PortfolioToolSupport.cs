using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using System.Text.Json;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>What the two portfolio tools share: the base currency argument, loading the saved portfolio, and the latest stored price of each holding.</summary>
internal static class PortfolioToolSupport
{
    public const string BaseCurrencyArgument = "baseCurrency";

    /// <summary>The latest stored bar of one holding: its close and its date.</summary>
    public readonly record struct LatestPrice(decimal Close, DateTime Date);

    /// <summary>The base currency of the call (default JPY, the application's default); a code that is not three letters is INVALID_ARGUMENT.</summary>
    public static CurrencyCode ReadBaseCurrency(JsonElement arguments)
    {
        string? text = ToolArgs.String(arguments, BaseCurrencyArgument);
        if (text is null)
        {
            return CurrencyCode.JPY;
        }

        string trimmed = text.Trim();
        if (trimmed.Length != 3 || !trimmed.All(char.IsAsciiLetter))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{BaseCurrencyArgument}' must be a three-letter ISO 4217 currency code.", BaseCurrencyArgument, "For example JPY or USD.");
        }

        return new CurrencyCode(trimmed);
    }

    /// <summary>The saved portfolio; an unreadable or half-saved one is DATA_UNAVAILABLE (retry), never an empty answer.</summary>
    public static async Task<PortfolioReadResult> LoadAsync(IReadOnlyPortfolioSource source, CancellationToken cancellationToken)
    {
        try
        {
            return await source.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DataUnavailableException)
        {
            throw new ToolException(ToolErrorCodes.DataUnavailable, "The saved portfolio is temporarily unreadable.", hint: "Retry in a moment.");
        }
    }

    /// <summary>The latest stored daily bar of every distinct ticker that has one; a ticker that is not stored or not well formed has no entry.</summary>
    public static async Task<Dictionary<string, LatestPrice>> LatestPricesAsync(IReadOnlyStockAnalyzerGateway gateway, IEnumerable<string> tickers, CancellationToken cancellationToken)
    {
        var prices = new Dictionary<string, LatestPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (string ticker in tickers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IReadOnlyList<CandleData> bars = await gateway.GetCandlesAsync(ticker, TimeFrame.D1, 1, cancellationToken).ConfigureAwait(false);
                if (bars.Count > 0)
                {
                    prices[ticker] = new LatestPrice(bars[^1].Close, bars[^1].Timestamp);
                }
            }
            catch (ArgumentException)
            {
                // A ticker the gateway refuses (not well formed) is simply unpriced.
            }
        }

        return prices;
    }
}
