using System;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlReplayScopeClassifierTests
{
    private static readonly DateOnly Start = new(2010, 1, 1);
    private static readonly DateOnly EndExclusive = new(2020, 1, 1);

    private static RlReplayScope Classify(string symbol, DateOnly first, DateOnly last, string generationSymbol = "AAA")
        => RlReplayScopeClassifier.Classify(generationSymbol, Start, EndExclusive, symbol, first, last);

    [Fact]
    public void AnotherInstrumentIsOtherWhateverTheDates()
    {
        Assert.Equal(RlReplayScope.OtherInstrument, Classify("BBB", new(2012, 1, 1), new(2012, 12, 1)));
        Assert.Equal(RlReplayScope.OtherInstrument, Classify("BBB", new(2000, 1, 1), new(2000, 12, 1)));
        Assert.Equal(RlReplayScope.OtherInstrument, Classify("BBB", new(2025, 1, 1), new(2025, 12, 1)));
    }

    [Fact]
    public void TheSymbolIsComparedIgnoringCaseAndSurroundingSpaces()
    {
        Assert.Equal(RlReplayScope.SameInstrumentOverlapsTraining, Classify(" aaa ", new(2012, 1, 1), new(2012, 12, 1)));
        Assert.Equal(RlReplayScope.SameInstrumentOverlapsTraining, Classify("AAA", new(2012, 1, 1), new(2012, 12, 1), generationSymbol: "AAA"));
    }

    [Fact]
    public void TheWindowInsideTheTrainingPeriodOverlapsIt()
        => Assert.Equal(RlReplayScope.SameInstrumentOverlapsTraining, Classify("AAA", new(2012, 1, 1), new(2012, 12, 1)));

    [Fact]
    public void TheWindowEndingTheDayBeforeTheTrainingPeriodIsBeforeIt()
    {
        Assert.Equal(RlReplayScope.SameInstrumentBeforeTraining, Classify("AAA", new(2009, 1, 1), Start.AddDays(-1)));
        // One day later the last bar is the first training day: the window overlaps.
        Assert.Equal(RlReplayScope.SameInstrumentOverlapsTraining, Classify("AAA", new(2009, 1, 1), Start));
    }

    [Fact]
    public void TheWindowStartingOnTheExclusiveEndIsAfterTheTrainingPeriod()
    {
        Assert.Equal(RlReplayScope.SameInstrumentAfterTraining, Classify("AAA", EndExclusive, new(2020, 12, 1)));
        // One day earlier the first bar is the last training day: the window overlaps.
        Assert.Equal(RlReplayScope.SameInstrumentOverlapsTraining, Classify("AAA", EndExclusive.AddDays(-1), new(2020, 12, 1)));
    }

    [Fact]
    public void AWindowCoveringTheWholeTrainingPeriodOverlapsIt()
        => Assert.Equal(RlReplayScope.SameInstrumentOverlapsTraining, Classify("AAA", new(2005, 1, 1), new(2025, 1, 1)));

    [Fact]
    public void TheTrainingSourceValueIsTheDefaultOfASnapshotAndIsNeverReturned()
    {
        Assert.Equal(RlReplayScope.TrainingSource, default);
        Assert.NotEqual(RlReplayScope.TrainingSource, Classify("AAA", new(2012, 1, 1), new(2012, 12, 1)));
    }

    // Source 2010-01-01 .. 2020-01-01 (exclusive); validation begins 2016-01-01 (index floor(3N/5)), final evaluation 2018-01-01 (index floor(4N/5)).
    private static readonly DateOnly ValidationStart = new(2016, 1, 1), FinalStart = new(2018, 1, 1);

    private static RlSourceParts Parts(DateOnly first, DateOnly last, string symbol = "AAA")
        => RlReplayScopeClassifier.OverlappingParts("AAA", Start, ValidationStart, FinalStart, EndExclusive, symbol, first, last);

    [Fact]
    public void AWindowInsideOnePartNamesOnlyThatPart()
    {
        Assert.Equal(RlSourceParts.Train, Parts(new(2012, 1, 1), new(2012, 12, 1)));
        Assert.Equal(RlSourceParts.Validation, Parts(new(2016, 6, 1), new(2017, 6, 1)));
        Assert.Equal(RlSourceParts.FinalEvaluation, Parts(new(2018, 6, 1), new(2019, 6, 1)));
    }

    [Fact]
    public void TheBoundaryDaysBelongToTheLaterPart()
    {
        // Ending the day before a cut stays in the earlier part; starting on the cut is in the later part only.
        Assert.Equal(RlSourceParts.Train, Parts(new(2015, 1, 1), ValidationStart.AddDays(-1)));
        Assert.Equal(RlSourceParts.Train | RlSourceParts.Validation, Parts(new(2015, 1, 1), ValidationStart));
        Assert.Equal(RlSourceParts.Validation, Parts(ValidationStart, FinalStart.AddDays(-1)));
        Assert.Equal(RlSourceParts.Validation | RlSourceParts.FinalEvaluation, Parts(ValidationStart.AddDays(30), FinalStart));
        Assert.Equal(RlSourceParts.FinalEvaluation, Parts(FinalStart, new(2019, 12, 31)));
        Assert.Equal(RlSourceParts.Train, Parts(Start, Start));
        Assert.Equal(RlSourceParts.FinalEvaluation, Parts(EndExclusive.AddDays(-1), EndExclusive.AddDays(40)));
    }

    [Fact]
    public void AWindowSpanningSeveralPartsNamesAllOfThem()
    {
        Assert.Equal(RlSourceParts.Train | RlSourceParts.Validation, Parts(new(2015, 6, 1), new(2016, 6, 1)));
        Assert.Equal(RlSourceParts.Validation | RlSourceParts.FinalEvaluation, Parts(new(2017, 6, 1), new(2018, 6, 1)));
        Assert.Equal(RlSourceParts.Train | RlSourceParts.Validation | RlSourceParts.FinalEvaluation, Parts(new(2009, 1, 1), new(2025, 1, 1)));
    }

    [Fact]
    public void OutsideTheSourcePeriodOrAnotherTickerNamesNoPart()
    {
        Assert.Equal(RlSourceParts.None, Parts(new(2009, 1, 1), Start.AddDays(-1)));
        Assert.Equal(RlSourceParts.None, Parts(EndExclusive, new(2020, 12, 1)));
        Assert.Equal(RlSourceParts.None, Parts(new(2012, 1, 1), new(2012, 12, 1), "BBB"));
        Assert.Equal(RlSourceParts.Train, Parts(new(2012, 1, 1), new(2012, 12, 1), " aaa "));
    }

    [Fact]
    public void ThePartsAgreeWithTheScopeForEveryWindow()
    {
        for (var day = new DateOnly(2008, 1, 1); day < new DateOnly(2022, 1, 1); day = day.AddDays(97))
        {
            var last = day.AddDays(191);
            bool overlaps = Classify("AAA", day, last) == RlReplayScope.SameInstrumentOverlapsTraining;
            Assert.Equal(overlaps, Parts(day, last) != RlSourceParts.None);
        }
    }

    [Theory]
    [InlineData(0, 0, 0)] [InlineData(5, 3, 4)] [InlineData(10, 6, 8)] [InlineData(11, 6, 8)] [InlineData(1_278, 766, 1_022)] [InlineData(5_200, 3_120, 4_160)]
    public void TheRatioCutsAreTheIntegerFloorsOfThreeFifthsAndFourFifths(int length, int cut1, int cut2)
        => Assert.Equal((cut1, cut2), RlPartitionCuts.Ratio(length));
}
