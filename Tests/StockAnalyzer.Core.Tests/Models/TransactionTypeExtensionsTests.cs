using System;
using StockAnalyzer.Core.Models.Portfolio;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// IsCashFlow is the one definition of "a Deposit or a Withdrawal". It is true for exactly those two types and false for every trade type
/// and for a stored value that is no member of the enum (the loader rejects such a value, but the helper must not report it as a cash flow).
/// </summary>
public sealed class TransactionTypeExtensionsTests
{
    [Theory]
    [InlineData(TransactionType.Deposit, true)]
    [InlineData(TransactionType.Withdrawal, true)]
    [InlineData(TransactionType.Long, false)]
    [InlineData(TransactionType.ExitLong, false)]
    [InlineData(TransactionType.Short, false)]
    [InlineData(TransactionType.ExitShort, false)]
    public void IsCashFlow_IsTrueOnlyForDepositAndWithdrawal(TransactionType type, bool expected) =>
        Assert.Equal(expected, type.IsCashFlow());

    [Fact]
    public void IsCashFlow_IsFalseForAnUndefinedValue() => Assert.False(((TransactionType)99).IsCashFlow());

    [Fact]
    public void IsCashFlow_CoversEveryDefinedTypeExplicitly()
    {
        // A type added to the enum later must be classified on purpose: this fails until the expected set below is revisited.
        var cashFlows = Array.FindAll(Enum.GetValues<TransactionType>(), t => t.IsCashFlow());

        Assert.Equal(new[] { TransactionType.Deposit, TransactionType.Withdrawal }, cashFlows);
    }
}
