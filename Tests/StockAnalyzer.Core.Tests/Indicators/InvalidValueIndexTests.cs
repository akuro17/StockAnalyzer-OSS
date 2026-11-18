using System;
using System.Linq;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class InvalidValueIndexTests
{
    private static InvalidValueIndex Build(bool[] invalid)
    {
        var index = new InvalidValueIndex(invalid.Length);
        foreach (bool value in invalid)
        {
            index.Add(value);
        }

        return index;
    }

    private static bool BruteForceClean(bool[] invalid, int first, int last)
    {
        for (int i = first; i <= last; i++)
        {
            if (invalid[i])
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public void EmptyIndex_BuildsWithoutAnyBar()
    {
        var index = new InvalidValueIndex(0);

        Assert.NotNull(index);
    }

    [Fact]
    public void AllClean_EveryWindowIsClean()
    {
        InvalidValueIndex index = Build(new bool[6]);

        for (int first = 0; first < 6; first++)
        {
            for (int last = first; last < 6; last++)
            {
                Assert.True(index.IsClean(first, last));
            }
        }
    }

    [Fact]
    public void AllInvalid_NoWindowIsClean()
    {
        bool[] all = Enumerable.Repeat(true, 6).ToArray();
        InvalidValueIndex index = Build(all);

        for (int first = 0; first < 6; first++)
        {
            for (int last = first; last < 6; last++)
            {
                Assert.False(index.IsClean(first, last));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void SingleInvalidBar_SpoilsExactlyTheWindowsThatContainIt(int position)
    {
        bool[] invalid = new bool[7];
        invalid[position] = true;
        InvalidValueIndex index = Build(invalid);

        for (int first = 0; first < 7; first++)
        {
            for (int last = first; last < 7; last++)
            {
                bool contains = first <= position && position <= last;
                Assert.Equal(!contains, index.IsClean(first, last));
            }
        }
    }

    [Fact]
    public void SingleBarWindow_ReportsThatBarOnly()
    {
        InvalidValueIndex index = Build(new[] { false, true, false });

        Assert.True(index.IsClean(0, 0));
        Assert.False(index.IsClean(1, 1));
        Assert.True(index.IsClean(2, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(64)]
    [InlineData(513)]
    public void RandomPatterns_EqualABruteForceLoop(int length)
    {
        var random = new Random(20261117 + length);
        bool[] invalid = new bool[length];
        for (int i = 0; i < length; i++)
        {
            // Sparse invalid bars so that both clean and spoiled windows occur.
            invalid[i] = random.NextDouble() < 0.1;
        }

        InvalidValueIndex index = Build(invalid);

        for (int first = 0; first < length; first++)
        {
            for (int last = first; last < length; last++)
            {
                Assert.Equal(BruteForceClean(invalid, first, last), index.IsClean(first, last));
            }
        }
    }
}
