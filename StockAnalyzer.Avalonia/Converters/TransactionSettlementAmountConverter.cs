using System;
using System.Globalization;
using Avalonia.Data.Converters;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Avalonia.Converters;

/// <summary>
/// Text of the Amount cell of the Transactions list: the signed cash effect of a transaction (<see cref="TransactionCashEffect"/>) with two decimals,
/// or an empty text for a value that is not a transaction or has no defined effect.
/// </summary>
public sealed class TransactionSettlementAmountConverter : IValueConverter
{
    public static readonly TransactionSettlementAmountConverter Instance = new();

    private const string AmountFormat = "F2";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Transaction transaction)
        {
            return string.Empty;
        }

        decimal? amount = TransactionCashEffect.GetSettlementAmount(transaction);
        return amount.HasValue ? amount.Value.ToString(AmountFormat, culture) : string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
