using System;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

public class SeasonalityMonthlyReturnsPresentationTests
{
    [Fact]
    public void Create_NullTable_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SeasonalityMonthlyReturnsPresentation.Create(null!, "Annual", "Avg"));
    }

    [Fact]
    public void Create_EmptyTable_ReturnsTheSharedEmptyPresentation()
    {
        SeasonalityMonthlyReturnsTable table = SeasonalityMonthlyReturnsEngine.Build(
            SeasonalityChartEngine.Analyze(
                Array.Empty<SeasonalitySeriesInput>(),
                new SeasonalityChartParameters(5)));

        SeasonalityMonthlyReturnsPresentation presentation =
            SeasonalityMonthlyReturnsPresentation.Create(table, "Annual", "Avg");

        Assert.Same(SeasonalityMonthlyReturnsPresentation.Empty, presentation);
        Assert.False(presentation.HasData);
        Assert.Empty(presentation.Rows);
        Assert.Empty(presentation.ColumnHeaders);
    }

    [Fact]
    public void Create_ProducesTwelveMonthRowsThenAnnual()
    {
        SeasonalityMonthlyReturnsPresentation presentation = Presentation(
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 6, 30), 110m));

        Assert.True(presentation.HasData);
        Assert.Equal(SeasonalityMonthlyReturnsTable.RowCount, presentation.Rows.Count);
        Assert.Equal("Jan", presentation.Rows[0].Header);
        Assert.Equal("Dec", presentation.Rows[11].Header);
        Assert.Equal("Annual", presentation.Rows[12].Header);
        Assert.False(presentation.Rows[0].IsAnnualRow);
        Assert.True(presentation.Rows[12].IsAnnualRow);
    }

    [Fact]
    public void Create_ColumnHeaders_AreTwoDigitYearLabelsThenTheAverageLabel()
    {
        SeasonalityMonthlyReturnsPresentation presentation = Presentation(
            (new DateTime(2022, 1, 3), 100m),
            (new DateTime(2022, 1, 31), 110m),
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 1, 31), 110m));

        Assert.Equal(new[] { "'22", "'23", "Avg" }, presentation.ColumnHeaders.ToArray());
        Assert.All(presentation.Rows, row => Assert.Equal(3, row.Cells.Count));
    }

    [Fact]
    public void Create_SignedCells_FormatAsPercentAndCarryTheColourBucket()
    {
        SeasonalityMonthlyReturnsPresentation presentation = Presentation(
            (new DateTime(2022, 1, 3), 100m),
            (new DateTime(2022, 1, 31), 110m),   // Jan 2022: +10%
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 1, 31), 90m),    // Jan 2023: -10%
            (new DateTime(2023, 2, 28), 90m));   // Feb 2023: 0%

        var january = presentation.Rows[0].Cells;
        Assert.Equal("+10.0%", january[0].Text);
        Assert.Equal(SeasonalityMonthlyReturnSign.Positive, january[0].Sign);
        Assert.Equal("-10.0%", january[1].Text);
        Assert.Equal(SeasonalityMonthlyReturnSign.Negative, january[1].Sign);
        Assert.Equal("0.0%", january[2].Text); // average of +10% and -10%
        Assert.Equal(SeasonalityMonthlyReturnSign.Neutral, january[2].Sign);

        var february = presentation.Rows[1].Cells;
        Assert.Equal(SeasonalityMonthlyReturnsPresentation.MissingCellText, february[0].Text); // no Feb 2022
        Assert.Equal(SeasonalityMonthlyReturnSign.Neutral, february[0].Sign);
        Assert.Equal("0.0%", february[1].Text);
        Assert.Equal(SeasonalityMonthlyReturnSign.Neutral, february[1].Sign);
    }

    [Fact]
    public void Create_UndefinedRow_ShowsDashesAndAnUndefinedAverage()
    {
        SeasonalityMonthlyReturnsPresentation presentation = Presentation(
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 6, 30), 110m));

        var march = presentation.Rows[2].Cells; // no March close anywhere
        Assert.All(march, cell => Assert.Equal(SeasonalityMonthlyReturnsPresentation.MissingCellText, cell.Text));
        Assert.All(march, cell => Assert.Equal(SeasonalityMonthlyReturnSign.Neutral, cell.Sign));
    }

    [Fact]
    public void Create_FrameWidth_TracksTheColumnsActuallyShownNotTheMaxOverlayDepth()
    {
        SeasonalityMonthlyReturnsPresentation presentation = Presentation(
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 6, 30), 120m));

        Assert.Equal(new[] { "'23", "Avg" }, presentation.ColumnHeaders.ToArray());
        Assert.All(presentation.Rows, row => Assert.Equal(2, row.Cells.Count));

        // One '23 column + the average column => two data columns, not MaxYearsToOverlay + 1.
        Assert.Equal(
            SeasonalityMonthlyReturnsMetrics.RowHeaderWidth
                + (2d * SeasonalityMonthlyReturnsMetrics.CellWidth),
            presentation.Frame.FrameWidth);
        Assert.Equal(
            (SeasonalityMonthlyReturnsMetrics.HeaderRowCount + SeasonalityMonthlyReturnsTable.RowCount)
                * SeasonalityMonthlyReturnsMetrics.CellHeight,
            presentation.Frame.FrameHeight);
    }

    [Fact]
    public void Create_TableFontAtOrBelowBaseline_KeepsTheBaselineCellGeometry()
    {
        SeasonalityMonthlyReturnsPresentation presentation = Presentation(
            StyleWithFontSize(10d),
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 6, 30), 120m));

        Assert.Equal(SeasonalityMonthlyReturnsMetrics.CellWidth, presentation.Frame.CellWidth);
        Assert.Equal(SeasonalityMonthlyReturnsMetrics.CellHeight, presentation.Frame.CellHeight);
        Assert.Equal(SeasonalityMonthlyReturnsMetrics.RowHeaderWidth, presentation.Frame.RowHeaderWidth);
    }

    [Fact]
    public void Create_TableFontAboveBaseline_ScalesCellAndFrameGeometry()
    {
        double fontSize = SeasonalityMonthlyReturnsMetrics.BaselineFontSize * 2d;

        SeasonalityMonthlyReturnsPresentation presentation = Presentation(
            StyleWithFontSize(fontSize),
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 6, 30), 120m));

        Assert.Equal(
            Math.Ceiling(SeasonalityMonthlyReturnsMetrics.CellWidth * 2d), presentation.Frame.CellWidth);
        Assert.Equal(
            Math.Ceiling(SeasonalityMonthlyReturnsMetrics.CellHeight * 2d), presentation.Frame.CellHeight);
        Assert.Equal(
            presentation.Frame.RowHeaderWidth + (2d * presentation.Frame.CellWidth),
            presentation.Frame.FrameWidth);
    }

    private static SeasonalityMonthlyReturnsStyle StyleWithFontSize(double fontSize)
        => SeasonalityMonthlyReturnsStyle.Default with { FontSize = fontSize };

    [Fact]
    public void Create_WithStyle_AppliesFontSizeAndSignBrushesToCells()
    {
        var style = new SeasonalityMonthlyReturnsStyle(
            PositiveBackground: new ImmutableSolidColorBrush(Colors.Green),
            NegativeBackground: new ImmutableSolidColorBrush(Colors.Red),
            NeutralBackground: new ImmutableSolidColorBrush(Colors.Gray),
            PositiveForeground: Brushes.Black,
            NegativeForeground: Brushes.White,
            NeutralForeground: Brushes.Yellow,
            FontSize: 19d);

        SeasonalityMonthlyReturnsPresentation presentation = Presentation(
            style,
            (new DateTime(2023, 1, 3), 100m),
            (new DateTime(2023, 1, 31), 110m),   // Jan 2023: +10%  -> Positive
            (new DateTime(2023, 2, 28), 90m));   // Feb 2023: -18%  -> Negative

        Assert.Equal(19d, presentation.FontSize);

        SeasonalityMonthlyReturnCellPresentation january = presentation.Rows[0].Cells[0];
        Assert.Equal(SeasonalityMonthlyReturnSign.Positive, january.Sign);
        Assert.Same(style.PositiveBackground, january.CellBackground);
        Assert.Same(style.PositiveForeground, january.CellForeground);

        SeasonalityMonthlyReturnCellPresentation february = presentation.Rows[1].Cells[0];
        Assert.Equal(SeasonalityMonthlyReturnSign.Negative, february.Sign);
        Assert.Same(style.NegativeBackground, february.CellBackground);

        SeasonalityMonthlyReturnCellPresentation march = presentation.Rows[2].Cells[0]; // missing -> Neutral
        Assert.Equal(SeasonalityMonthlyReturnSign.Neutral, march.Sign);
        Assert.Same(style.NeutralBackground, march.CellBackground);
        Assert.Same(style.NeutralForeground, march.CellForeground);
    }

    [Fact]
    public void Create_ThreeArgOverload_UsesTheDefaultStyle()
    {
        SeasonalityMonthlyReturnsPresentation presentation = Presentation(
            (new DateTime(2023, 1, 2), 100m),
            (new DateTime(2023, 6, 30), 110m));

        Assert.Equal(SeasonalityMonthlyReturnsStyle.Default.FontSize, presentation.FontSize);
        Assert.All(presentation.Rows, row => Assert.All(row.Cells, cell =>
        {
            Assert.NotNull(cell.CellBackground);
            Assert.NotNull(cell.CellForeground);
        }));
    }

    private static SeasonalityMonthlyReturnsPresentation Presentation(params (DateTime Timestamp, decimal? Value)[] points)
        => Presentation(SeasonalityMonthlyReturnsStyle.Default, points);

    private static SeasonalityMonthlyReturnsPresentation Presentation(
        SeasonalityMonthlyReturnsStyle style,
        params (DateTime Timestamp, decimal? Value)[] points)
    {
        var mapped = points
            .Select(point => new SeasonalityPoint(point.Timestamp, point.Value))
            .ToArray();
        var series = new SeasonalitySeriesInput(0, "BASE", SeasonalityRadiusMode.PercentVsYearStart, mapped);
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { series },
            new SeasonalityChartParameters(5));
        return SeasonalityMonthlyReturnsPresentation.Create(
            SeasonalityMonthlyReturnsEngine.Build(result),
            "Annual",
            "Avg",
            style);
    }

    [Fact]
    public void Create_WithMonthsAsColumnsOrientation_TransposesRowsAndColumns()
    {
        var points = new[]
        {
            new SeasonalityPoint(new DateTime(2022, 1, 3), 100m),
            new SeasonalityPoint(new DateTime(2022, 1, 31), 110m),
            new SeasonalityPoint(new DateTime(2023, 1, 2), 100m),
            new SeasonalityPoint(new DateTime(2023, 1, 31), 90m),
        };
        var series = new SeasonalitySeriesInput(0, "BASE", SeasonalityRadiusMode.PercentVsYearStart, points);
        SeasonalityChartResult result = SeasonalityChartEngine.Analyze(
            new[] { series },
            new SeasonalityChartParameters(5));
        SeasonalityMonthlyReturnsTable table = SeasonalityMonthlyReturnsEngine.Build(result);

        SeasonalityMonthlyReturnsPresentation presentation = SeasonalityMonthlyReturnsPresentation.Create(
            table,
            "Annual",
            "Avg",
            SeasonalityMonthlyReturnsStyle.Default,
            SeasonalityMonthlyTableOrientation.MonthsAsColumns);

        Assert.True(presentation.HasData);
        // ColumnHeaders: 12 months + 1 Annual = 13 columns
        Assert.Equal(13, presentation.ColumnHeaders.Count);
        Assert.Equal("Jan", presentation.ColumnHeaders[0]);
        Assert.Equal("Dec", presentation.ColumnHeaders[11]);
        Assert.Equal("Annual", presentation.ColumnHeaders[12]);

        // Rows: 2 years ('22, '23) + 1 Avg = 3 rows
        Assert.Equal(3, presentation.Rows.Count);
        Assert.Equal("'22", presentation.Rows[0].Header);
        Assert.Equal("'23", presentation.Rows[1].Header);
        Assert.Equal("Avg", presentation.Rows[2].Header);
        Assert.False(presentation.Rows[0].IsAnnualRow);
        Assert.True(presentation.Rows[2].IsAnnualRow);

        // Cells: each row has 13 cells
        Assert.All(presentation.Rows, row => Assert.Equal(13, row.Cells.Count));
        Assert.Equal("+10.0%", presentation.Rows[0].Cells[0].Text);
        Assert.Equal("-10.0%", presentation.Rows[1].Cells[0].Text);
        Assert.Equal("0.0%", presentation.Rows[2].Cells[0].Text);
    }
}
