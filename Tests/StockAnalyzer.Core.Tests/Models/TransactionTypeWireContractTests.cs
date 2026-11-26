using System;
using System.Linq;
using StockAnalyzer.Core.Models.Portfolio;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// <see cref="TransactionType"/> is stored as its integer in the portfolio Parquet files (SA_ARCHITECTURE_RULES, "Persisted Enums Carry Explicit Integer Values"):
/// the name-to-number table is append-only. Changing a number, or adding a member, must fail here first so that the stored data and the
/// load-time <c>Enum.IsDefined</c> check are reviewed together.
/// </summary>
public sealed class TransactionTypeWireContractTests
{
    [Theory]
    [InlineData(nameof(TransactionType.Long), 0)]
    [InlineData(nameof(TransactionType.Buy), 0)]
    [InlineData(nameof(TransactionType.ExitLong), 1)]
    [InlineData(nameof(TransactionType.Sell), 1)]
    [InlineData(nameof(TransactionType.Deposit), 2)]
    [InlineData(nameof(TransactionType.Withdrawal), 3)]
    [InlineData(nameof(TransactionType.Short), 4)]
    [InlineData(nameof(TransactionType.ExitShort), 5)]
    [InlineData(nameof(TransactionType.Cover), 5)]
    public void EveryMemberKeepsItsStoredNumber(string name, int expected)
    {
        Assert.Equal(expected, (int)Enum.Parse<TransactionType>(name));
    }

    [Fact]
    public void ThereAreExactlyTheNineKnownNamesAndTheNumbersZeroToFive()
    {
        Assert.Equal(9, Enum.GetNames<TransactionType>().Length);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Enum.GetValues<TransactionType>().Select(v => (int)v).Distinct().OrderBy(v => v).ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(99)]
    public void ANumberOutsideTheTableIsNoMember(int number)
    {
        Assert.False(Enum.IsDefined(typeof(TransactionType), number));
    }
}
