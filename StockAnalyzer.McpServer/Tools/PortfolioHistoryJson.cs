using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// The two history lists of sa_get_portfolio_summary (<c>includeClosedPositions</c>, <c>includeTransactions</c>): the closed positions and the transactions of the saved portfolio,
/// newest first, each as <c>{ totalCount, returnedCount, truncated, items }</c>. Pure writers over the portfolio object the tool already holds; nothing is read or calculated here.
/// The order is total (ties are broken by the id), so the same portfolio always gives the same list. Amounts are written as stored (decimals, unrounded).
/// </summary>
internal static class PortfolioHistoryJson
{
    /// <summary>
    /// The stable name of a transaction type. <see cref="TransactionType"/> has aliased members (Long = Buy, ExitLong = Sell, ExitShort = Cover), so its own ToString is not stable;
    /// one explicit mapping is used. A value that is no member is written as its number.
    /// </summary>
    public static string TransactionTypeName(TransactionType type) => type switch
    {
        TransactionType.Long => "Long",
        TransactionType.ExitLong => "ExitLong",
        TransactionType.Deposit => "Deposit",
        TransactionType.Withdrawal => "Withdrawal",
        TransactionType.Short => "Short",
        TransactionType.ExitShort => "ExitShort",
        _ => ((int)type).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Closed positions in the written order: exit time (newest first), then entry time (newest first), then the id as text (ordinal, ascending).</summary>
    public static IReadOnlyList<ClosedPosition> OrderClosedPositions(IEnumerable<ClosedPosition> closedPositions)
        => closedPositions
            .OrderByDescending(p => p.ExitTime)
            .ThenByDescending(p => p.EntryTime)
            .ThenBy(p => IdText(p.Id), StringComparer.Ordinal)
            .ToList();

    /// <summary>Transactions in the written order: execution time (newest first), then the id as text (ordinal, ascending).</summary>
    public static IReadOnlyList<Transaction> OrderTransactions(IEnumerable<Transaction> transactions)
        => transactions
            .OrderByDescending(t => t.ExecutedAt)
            .ThenBy(t => IdText(t.Id), StringComparer.Ordinal)
            .ToList();

    /// <summary>Writes the <c>closedPositions</c> property: the first <paramref name="limit"/> of the ordered list and the counts.</summary>
    public static void WriteClosedPositions(Utf8JsonWriter writer, IReadOnlyList<ClosedPosition> closedPositions, int limit)
    {
        IReadOnlyList<ClosedPosition> ordered = OrderClosedPositions(closedPositions);
        int returned = Math.Min(limit, ordered.Count);
        writer.WriteStartObject("closedPositions");
        WriteEnvelope(writer, ordered.Count, returned);
        writer.WriteStartArray("items");
        for (int i = 0; i < returned; i++)
        {
            ClosedPosition position = ordered[i];
            writer.WriteStartObject();
            writer.WriteString("id", IdText(position.Id));
            writer.WriteString("ticker", position.Ticker);
            writer.WriteString("side", TransactionTypeName(position.Type));
            writer.WriteNumber("quantity", position.Quantity);
            writer.WriteNumber("entryPrice", position.EntryPrice);
            writer.WriteNumber("exitPrice", position.ExitPrice);
            writer.WriteString("entryTime", ToolJson.FormatTimestampUtc(position.EntryTime));
            writer.WriteString("exitTime", ToolJson.FormatTimestampUtc(position.ExitTime));
            writer.WriteNumber("realizedPnL", position.RealizedPnL);
            writer.WriteNumber("totalFees", position.TotalFees);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the <c>transactions</c> property: the first <paramref name="limit"/> of the ordered list and the counts. The notes, target price and stop loss of a transaction are
    /// the user's own annotations and are never written. A currency that is not set (a legacy row) is left out.
    /// </summary>
    public static void WriteTransactions(Utf8JsonWriter writer, IReadOnlyList<Transaction> transactions, int limit)
    {
        IReadOnlyList<Transaction> ordered = OrderTransactions(transactions);
        int returned = Math.Min(limit, ordered.Count);
        writer.WriteStartObject("transactions");
        WriteEnvelope(writer, ordered.Count, returned);
        writer.WriteStartArray("items");
        for (int i = 0; i < returned; i++)
        {
            Transaction transaction = ordered[i];
            writer.WriteStartObject();
            writer.WriteString("id", IdText(transaction.Id));
            writer.WriteString("executedAt", ToolJson.FormatTimestampUtc(transaction.ExecutedAt));
            writer.WriteString("type", TransactionTypeName(transaction.Type));
            if (transaction.Ticker is not null)
            {
                writer.WriteString("ticker", transaction.Ticker);
            }

            writer.WriteNumber("quantity", transaction.Quantity);
            writer.WriteNumber("pricePerUnit", transaction.PricePerUnit);
            WriteCurrency(writer, "priceCurrency", transaction.Price.Currency);
            writer.WriteNumber("cashAmount", transaction.CashAmount);
            writer.WriteNumber("fee", transaction.Fee);
            WriteCurrency(writer, "commissionCurrency", transaction.Commission.Currency);
            if (transaction.AppliedRate is { } rate)
            {
                writer.WriteStartObject("appliedRate");
                writer.WriteString("baseCurrency", rate.BaseCurrency.Value);
                writer.WriteString("quoteCurrency", rate.QuoteCurrency.Value);
                writer.WriteNumber("rate", rate.Rate);
                writer.WriteString("asOf", ToolJson.FormatDate(rate.AsOf));
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteEnvelope(Utf8JsonWriter writer, int totalCount, int returnedCount)
    {
        writer.WriteNumber("totalCount", totalCount);
        writer.WriteNumber("returnedCount", returnedCount);
        writer.WriteBoolean("truncated", returnedCount < totalCount);
    }

    private static void WriteCurrency(Utf8JsonWriter writer, string name, CurrencyCode currency)
    {
        if (!string.IsNullOrEmpty(currency.Value))
        {
            writer.WriteString(name, currency.Value);
        }
    }

    private static string IdText(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}
