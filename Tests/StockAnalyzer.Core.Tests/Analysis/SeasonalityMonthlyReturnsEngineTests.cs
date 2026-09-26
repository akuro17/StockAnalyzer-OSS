using System;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class SeasonalityMonthlyReturnsEngineTests
{
    [Fact]
    public void SeasonalityDisplayMode_DefaultsToPolarClock()
    {
        Assert.Equal(SeasonalityDisplayMode.PolarClock, default(SeasonalityDisplayMode));
    }

    [Fact]
    public void Build_NullResult_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SeasonalityMonthlyReturnsEngine.Build(null!));
    }

    [Fact]
    public void Build_ResultWithNoBaseSymbolTrace_ReturnsEmptyTable()
    {
        SeasonalityChartResult empty = SeasonalityChartEngine.Analyze(
            Array.Empty<SeasonalitySeriesInput>(),
            new SeasonalityChartParameters(5));

        SeasonalityMonthlyReturnsTable table = SeasonalityMonthlyReturnsEngine.Build(empty);

        Assert.Empty(table.Years);
        Assert.Equal(SeasonalityMonthlyReturnsTable.RowCount, table.RowAverages.Count);
        Assert.All(table.RowAverages, average => Assert.Null(average));
    }

    [Fact]
    public void Build_MonthCell_IsMonthEndCloseOverThePriorMonthEndClose()
    {
        SeasonalityMonthlyReturnsTable table = SeasonalityMonthlyReturnsEngine.Build(Analyze(
            5,
            (new DateTime(2023, 1, 1), 100m),
            (new DateTime(2023, 1, 31), 110m),
            (new DateTime(2023, 2, 28), 132m),
            (new DateTime(2023, 3, 31), 118.8m),
            (new DateTime(2023, 5, 31), 118.8m),
            (new DateTime(2023, 12, 29), 148.5m)));

        Assert.Equal(new[] { 2023 }, table.Years.ToArray());
        Assert.Equal(0.1m, table.Cell(0, 0).Return);   // Jan: 110 / 100 (year base) - 1
        Assert.Equal(0.2m, table.Cell(1, 0).Return);   // Feb: 132 / 110 - 1
        Assert.Equal(-0.1m, table.Cell(2, 0).Return);  // Mar: 118.8 / 132 - 1
        Assert.Null(table.Cell(3, 0).Return);          // Apr: no close
        Assert.Equal(0m, table.Cell(4, 0).Return);     // May: 118.8 / 118.8 (prior = Mar) - 1
        Assert.Equal(0.25m, table.Cell(11, 0).Return); // Dec: 148.5 / 118.8 (prior = May) - 1
    }

    [Fact]
    public void Build_AnnualRow_IsYearStartToYearEndChange()
    {
        SeasonalityMonthlyReturnsTable table = SeasonalityMonthlyReturnsEngine.Build(Analyze(
            5,
            (new DateTime(2023, 1, 1), 100m),
            (new DateTime(2023, 6, 30), 110m),
            (new DateTime(2023, 12, 29), 148.5m)));

        Assert.Equal(0.485m, table.Cell(SeasonalityMonthlyReturnsTable.AnnualRowIndex, 0).Return);
    }

    [Fact]
    public void AnnualReturn_NullTrace_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SeasonalityMonthlyReturnsEngine.AnnualReturn(null!));
    }

    [Fact]
    public void AnnualReturn_MatchesTheAnnualRowForTheSameYearTrace()
    {
        SeasonalityChartResult result = Analyze(
            5,
            (new DateTime(2023, 1, 1), 100m),
            (new DateTime(2023, 6, 30), 110m),
            (new DateTime(2023, 12, 29), 148.5m));
        SeasonalityYearTrace trace = Assert.Single(result.Traces);

        Assert.Equal(0.485m, SeasonalityMonthlyReturnsEngine.AnnualReturn(trace));
        Assert.Equal(
            SeasonalityMonthlyReturnsEngine.Build(result).Cell(SeasonalityMonthlyReturnsTable.AnnualRowIndex, 0).Return,
            SeasonalityMonthlyReturnsEngine.AnnualReturn(trace));
    }

    [Fact]
    public void Build_MissingMonth_LeavesTheCellUndefinedNotZero()
    {
        SeasonalityMonthlyReturnsTable table = SeasonalityMonthlyReturnsEngine.Build(Analyze(
            5,
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 6, 30), 120m)));

        Assert.False(table.Cell(1, 0).IsDefined); // Feb
        Assert.Null(table.Cell(1, 0).Return);
        Assert.Equal(0.2m, table.Cell(5, 0).Return); // Jun: 120 / 100 (prior = Jan close) - 1
    }

    [Fact]
    public void Build_RowAverage_IsArithmeticMeanOverYearsWhereTheCellIsDefined()
    {
        SeasonalityMonthlyReturnsTable table = SeasonalityMonthlyReturnsEngine.Build(Analyze(
            5,
            // 2022: January, February, December only (no March, no April)
            (new DateTime(2022, 1, 3), 200m),
            (new DateTime(2022, 1, 28), 220m),
            (new DateTime(2022, 2, 25), 209m),
            (new DateTime(2022, 12, 30), 250.8m),
            // 2023: January, February, March, December (no April)
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 1, 31), 110m),
            (new DateTime(2023, 2, 28), 132m),
            (new DateTime(2023, 3, 31), 118.8m),
            (new DateTime(2023, 12, 29), 148.5m)));

        Assert.Equal(new[] { 2022, 2023 }, table.Years.ToArray());
        Assert.Equal(0.10m, table.RowAverages[0]);   // Jan: (0.10 + 0.10) / 2
        Assert.Equal(0.075m, table.RowAverages[1]);  // Feb: (-0.05 + 0.20) / 2
        Assert.Equal(-0.10m, table.RowAverages[2]);  // Mar: defined in 2023 only
        Assert.Null(table.RowAverages[3]);           // Apr: defined in neither year
    }

    [Fact]
    public void Build_SingleYear_RowAverageEqualsThatYearsCell()
    {
        SeasonalityMonthlyReturnsTable table = SeasonalityMonthlyReturnsEngine.Build(Analyze(
            5,
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 1, 31), 110m)));

        Assert.Equal(0.1m, table.Cell(0, 0).Return);
        Assert.Equal(table.Cell(0, 0).Return, table.RowAverages[0]);
    }

    [Fact]
    public void Build_LeapYear_DecemberCellUsesTheDecember31Close()
    {
        SeasonalityMonthlyReturnsTable table = SeasonalityMonthlyReturnsEngine.Build(Analyze(
            5,
            (new DateTime(2024, 1, 1), 100m),
            (new DateTime(2024, 11, 29), 100m),
            (new DateTime(2024, 12, 31), 150m)));

        Assert.Equal(new[] { 2024 }, table.Years.ToArray());
        Assert.Equal(0.5m, table.Cell(11, 0).Return); // Dec: 150 / 100 (prior = Nov) - 1
        Assert.Equal(0.5m, table.Cell(SeasonalityMonthlyReturnsTable.AnnualRowIndex, 0).Return);
    }

    [Fact]
    public void Build_YearWithNoPositiveBaseClose_LeavesTheWholeColumnUndefined()
    {
        SeasonalityMonthlyReturnsTable table = SeasonalityMonthlyReturnsEngine.Build(Analyze(
            5,
            (new DateTime(2023, 1, 2), 0m),
            (new DateTime(2023, 2, 2), -5m),
            (new DateTime(2023, 3, 2), -1m)));

        for (int row = 0; row < SeasonalityMonthlyReturnsTable.RowCount; row++)
        {
            Assert.Null(table.Cell(row, 0).Return);
        }
    }

    [Fact]
    public void Build_DoesNotMutateTheSourceResult()
    {
        SeasonalityChartResult result = Analyze(
            5,
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 6, 30), 120m));
        int tracesBefore = result.Traces.Count;
        double minRadiusBefore = result.MinRadius;
        int[] yearsBefore = result.CalendarYears.ToArray();

        SeasonalityMonthlyReturnsEngine.Build(result);
        SeasonalityMonthlyReturnsEngine.Build(result);

        Assert.Equal(tracesBefore, result.Traces.Count);
        Assert.Equal(minRadiusBefore, result.MinRadius);
        Assert.Equal(yearsBefore, result.CalendarYears.ToArray());
    }

    private static SeasonalityChartResult Analyze(uint years, params (DateTime Timestamp, decimal? Value)[] points)
    {
        var mapped = new SeasonalityPoint[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            mapped[i] = new SeasonalityPoint(points[i].Timestamp, points[i].Value);
        }

        var series = new SeasonalitySeriesInput(0, "BASE", SeasonalityRadiusMode.PercentVsYearStart, mapped);
        return SeasonalityChartEngine.Analyze(new[] { series }, new SeasonalityChartParameters(years));
    }
}
