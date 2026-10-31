using System;
using Xunit;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>Pins <see cref="SeriesArithmetic"/> to hand-computed values: every operation, null propagation, zero divisors and values beyond decimal range.</summary>
public class SeriesArithmeticTests
{
    private static decimal?[] Series(params decimal?[] values) => values;

    [Theory]
    [InlineData(SeriesOperation.Add, 7.5, 2.5, 10.0)]
    [InlineData(SeriesOperation.Subtract, 7.5, 2.5, 5.0)]
    [InlineData(SeriesOperation.Multiply, 7.5, 2.5, 18.75)]
    [InlineData(SeriesOperation.Divide, 7.5, 2.5, 3.0)]
    [InlineData(SeriesOperation.Max, 7.5, 2.5, 7.5)]
    [InlineData(SeriesOperation.Min, 7.5, 2.5, 2.5)]
    [InlineData(SeriesOperation.Subtract, 2.5, 7.5, -5.0)]
    [InlineData(SeriesOperation.Multiply, -2, 3, -6)]
    [InlineData(SeriesOperation.Divide, -9, 3, -3)]
    [InlineData(SeriesOperation.Divide, 1, 4, 0.25)]
    [InlineData(SeriesOperation.Max, -1, -2, -1)]
    [InlineData(SeriesOperation.Min, -1, -2, -2)]
    [InlineData(SeriesOperation.Max, 3, 3, 3)]
    [InlineData(SeriesOperation.Min, 3, 3, 3)]
    public void OneBar_EqualsTheHandComputedValue(SeriesOperation operation, double left, double right, double expected)
    {
        decimal? actual = SeriesArithmetic.Apply(operation, (decimal)left, (decimal)right, out bool divisionByZero);

        Assert.Equal((decimal)expected, actual);
        Assert.False(divisionByZero);
    }

    [Theory]
    [InlineData(SeriesOperation.Add)]
    [InlineData(SeriesOperation.Subtract)]
    [InlineData(SeriesOperation.Multiply)]
    [InlineData(SeriesOperation.Divide)]
    [InlineData(SeriesOperation.Max)]
    [InlineData(SeriesOperation.Min)]
    public void ABarWithoutAValueOnEitherSideHasNoValue(SeriesOperation operation)
    {
        Assert.Null(SeriesArithmetic.Apply(operation, null, 2m, out bool a));
        Assert.Null(SeriesArithmetic.Apply(operation, 2m, null, out bool b));
        Assert.Null(SeriesArithmetic.Apply(operation, null, null, out bool c));
        Assert.False(a || b || c);
    }

    [Fact]
    public void Divide_ByZero_HasNoValueAndIsCountedOnlyWhenBothSidesHaveAValue()
    {
        DerivedSeries result = SeriesArithmetic.Apply(
            SeriesOperation.Divide,
            Series(1m, 2m, null, 4m, 5m),
            Series(0m, 2m, 0m, 0m, null),
            5);

        Assert.Equal(new decimal?[] { null, 1m, null, null, null }, result.Values);
        Assert.Equal(2, result.DivisionByZeroCount); // bars 0 and 3; bar 2 has no left value, bar 4 no right value
    }

    [Fact]
    public void ZeroOverANonZeroValueIsZero_AndZeroOverZeroIsADivisionByZero()
    {
        Assert.Equal(0m, SeriesArithmetic.Apply(SeriesOperation.Divide, 0m, 5m, out bool first));
        Assert.Null(SeriesArithmetic.Apply(SeriesOperation.Divide, 0m, 0m, out bool second));
        Assert.False(first);
        Assert.True(second);
    }

    [Fact]
    public void OtherOperationsWithAZeroOperandAreNotDivisionsByZero()
    {
        DerivedSeries result = SeriesArithmetic.Apply(SeriesOperation.Multiply, Series(0m, 3m), Series(0m, 0m), 2);

        Assert.Equal(new decimal?[] { 0m, 0m }, result.Values);
        Assert.Equal(0, result.DivisionByZeroCount);
    }

    [Theory]
    [InlineData(SeriesOperation.Add)]
    [InlineData(SeriesOperation.Subtract)]
    [InlineData(SeriesOperation.Multiply)]
    [InlineData(SeriesOperation.Divide)]
    public void AResultBeyondDecimalRangeHasNoValueInsteadOfThrowing(SeriesOperation operation)
    {
        // MaxValue + MaxValue, MaxValue - (-MaxValue), MaxValue * 2 and MaxValue / 0.1 all exceed the range.
        decimal left = decimal.MaxValue;
        decimal right = operation switch
        {
            SeriesOperation.Add => decimal.MaxValue,
            SeriesOperation.Subtract => decimal.MinValue,
            SeriesOperation.Multiply => 2m,
            _ => 0.1m,
        };

        decimal? actual = SeriesArithmetic.Apply(operation, left, right, out bool divisionByZero);

        Assert.Null(actual);
        Assert.False(divisionByZero);
    }

    [Fact]
    public void ASeriesShorterThanTheLength_HasNoValueAtTheMissingBars_AndLongerEntriesAreIgnored()
    {
        DerivedSeries result = SeriesArithmetic.Apply(
            SeriesOperation.Add,
            Series(1m, 2m, 3m, 4m, 5m, 6m),
            Series(10m, 20m),
            4);

        Assert.Equal(4, result.Values.Count);
        Assert.Equal(new decimal?[] { 11m, 22m, null, null }, result.Values);
    }

    [Fact]
    public void ALengthOfZeroGivesAnEmptySeries()
    {
        DerivedSeries result = SeriesArithmetic.Apply(SeriesOperation.Add, Series(1m), Series(1m), 0);

        Assert.Empty(result.Values);
        Assert.Equal(0, result.DivisionByZeroCount);
    }

    [Fact]
    public void ANegativeLengthIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SeriesArithmetic.Apply(SeriesOperation.Add, Series(1m), Series(1m), -1));
    }

    [Fact]
    public void NullSeriesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => SeriesArithmetic.Apply(SeriesOperation.Add, null!, Series(1m), 1));
        Assert.Throws<ArgumentNullException>(() => SeriesArithmetic.Apply(SeriesOperation.Add, Series(1m), null!, 1));
    }

    [Fact]
    public void AnUnknownOperationValueIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SeriesArithmetic.Apply((SeriesOperation)99, 1m, 1m, out _));
    }

    [Fact]
    public void TheOperationNamesAreTheNamesAClientWrites()
    {
        Assert.Equal(new[] { "Add", "Subtract", "Multiply", "Divide", "Max", "Min" }, Enum.GetNames<SeriesOperation>());
    }

    [Fact]
    public void ThePrecisionOfTheOperandsIsKept()
    {
        Assert.Equal(0.3m, SeriesArithmetic.Apply(SeriesOperation.Add, 0.1m, 0.2m, out _)); // exact in decimal, not 0.30000000000000004
    }
}
