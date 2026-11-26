using System;
using System.Globalization;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Core.Models.Portfolio;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Converters;

public class TransactionSettlementAmountConverterTests
{
    private static string Convert(object? value) =>
        (string)TransactionSettlementAmountConverter.Instance.Convert(value, typeof(string), null, CultureInfo.InvariantCulture)!;

    [Fact]
    public void ATrade_ShowsItsSignedCashEffectWithTwoDecimals()
    {
        var buy = new Transaction(DateTimeOffset.UtcNow.AddDays(-1), TransactionType.Long, "AAPL", 10m, 150.25m, 0m, 1m);

        Assert.Equal("-1503.50", Convert(buy));
    }

    [Fact]
    public void ADeposit_ShowsItsAmount()
    {
        var deposit = new Transaction(DateTimeOffset.UtcNow.AddDays(-1), TransactionType.Deposit, null, 0m, 0m, 1000m);

        Assert.Equal("1000.00", Convert(deposit));
    }

    [Fact]
    public void ATypeWithoutAnEffect_AndAForeignValue_ShowNothing()
    {
        var unknown = new Transaction(DateTimeOffset.UtcNow.AddDays(-1), (TransactionType)99, null, 0m, 0m, 0m);

        Assert.Equal(string.Empty, Convert(unknown));
        Assert.Equal(string.Empty, Convert("not a transaction"));
        Assert.Equal(string.Empty, Convert(null));
    }

    [Fact]
    public void ConvertBack_IsNotSupported() =>
        Assert.Throws<NotSupportedException>(() => TransactionSettlementAmountConverter.Instance.ConvertBack("1", typeof(Transaction), null, CultureInfo.InvariantCulture));
}
