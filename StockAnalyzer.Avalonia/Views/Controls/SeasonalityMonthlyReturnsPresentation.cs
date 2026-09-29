using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>Colour bucket for one monthly-returns cell.</summary>
public enum SeasonalityMonthlyReturnSign
{
    /// <summary>Zero return, or an undefined cell — neutral colour.</summary>
    Neutral,

    /// <summary>Positive return — up (bullish) colour.</summary>
    Positive,

    /// <summary>Negative return — down (bearish) colour.</summary>
    Negative
}

/// <summary>
/// Resolved visual style for the monthly-returns table, supplied by the view model from the
/// Seasonality settings page: one background/foreground brush pair per sign bucket and the cell font
/// size (points). Brushes are pre-resolved so the table view binds them directly with no converter.
/// </summary>
public readonly record struct SeasonalityMonthlyReturnsStyle(
    IBrush PositiveBackground,
    IBrush NegativeBackground,
    IBrush NeutralBackground,
    IBrush PositiveForeground,
    IBrush NegativeForeground,
    IBrush NeutralForeground,
    double FontSize)
{
    /// <summary>
    /// Fallback style built from <see cref="ChartSettingsConstants"/> defaults — used only where no
    /// settings-driven style is supplied (the three-argument <see cref="SeasonalityMonthlyReturnsPresentation.Create"/>
    /// overload, i.e. tests and design-time).
    /// </summary>
    public static SeasonalityMonthlyReturnsStyle Default { get; } = new(
        PositiveBackground: new ImmutableSolidColorBrush(Color.Parse(ChartSettingsConstants.DefaultSeasonalityMonthlyUpColor)),
        NegativeBackground: new ImmutableSolidColorBrush(Color.Parse(ChartSettingsConstants.DefaultSeasonalityMonthlyDownColor)),
        NeutralBackground: new ImmutableSolidColorBrush(Color.Parse(ChartSettingsConstants.DefaultSeasonalityMonthlyNeutralColor)),
        PositiveForeground: Brushes.White,
        NegativeForeground: Brushes.White,
        NeutralForeground: Brushes.White,
        FontSize: ChartSettingsConstants.DefaultSeasonalityFontSize);

    /// <summary>The background / foreground pair for one sign bucket.</summary>
    public (IBrush Background, IBrush Foreground) Resolve(SeasonalityMonthlyReturnSign sign) => sign switch
    {
        SeasonalityMonthlyReturnSign.Positive => (PositiveBackground, PositiveForeground),
        SeasonalityMonthlyReturnSign.Negative => (NegativeBackground, NegativeForeground),
        _ => (NeutralBackground, NeutralForeground),
    };
}

/// <summary>
/// Resolved pixel geometry for one monthly-returns table frame: the cell / row-header size after
/// the table-font scale, and the total frame the scrolled content occupies. Every value is a plain
/// double so the view binds it with no converter.
/// </summary>
public readonly record struct SeasonalityMonthlyReturnsFrame(
    double CellWidth,
    double CellHeight,
    double RowHeaderWidth,
    double FrameWidth,
    double FrameHeight);

/// <summary>
/// Pixel metrics for the monthly-returns table. The base cell dimensions are authored against
/// <see cref="BaselineFontSize"/>; <see cref="Resolve"/> scales them up for a larger table font and
/// sizes the frame to the columns actually shown (confirmation C2 / S4c — the frame grows with the
/// font and never slots the maximum overlay depth into a fixed width). Keeps magic numbers out of
/// XAML.
/// </summary>
public static class SeasonalityMonthlyReturnsMetrics
{
    /// <summary>
    /// Width of one year / average data column at <see cref="BaselineFontSize"/>. Sized for the widest
    /// formatted cell — a four-digit signed percent (<c>-1234.5%</c>) — plus centering margin and the
    /// 1 px cell rule, so three- and four-digit returns are not clipped.
    /// </summary>
    public const double CellWidth = 68d;

    /// <summary>Height of every row, the column-header row included, at <see cref="BaselineFontSize"/>.</summary>
    public const double CellHeight = 22d;

    /// <summary>Width of the left-hand row-label column at <see cref="BaselineFontSize"/>.</summary>
    public const double RowHeaderWidth = 62d;

    /// <summary>The single column-header row above the twelve month rows and the annual row.</summary>
    public const int HeaderRowCount = 1;

    /// <summary>
    /// Font size the base cell dimensions are drawn for. A table font at or below this keeps the base
    /// geometry (confirmation C2 — the baseline frame is the floor; it never shrinks below the size a
    /// 14 pt table needs for the same column count).
    /// </summary>
    public static readonly double BaselineFontSize = ChartSettingsConstants.DefaultSeasonalityFontSize;

    /// <summary>
    /// Linear scale applied to the base cell geometry for <paramref name="fontSize"/>. Floored at 1:
    /// a font at or under <see cref="BaselineFontSize"/> (or a non-finite value) keeps the base size.
    /// </summary>
    public static double FontScale(double fontSize)
        => double.IsNaN(fontSize) || double.IsInfinity(fontSize) || fontSize <= BaselineFontSize
            ? 1d
            : fontSize / BaselineFontSize;

    /// <summary>
    /// Resolves the frame geometry for <paramref name="tableFontSize"/>, <paramref name="columnCount"/>
    /// and <paramref name="rowCount"/>.
    /// </summary>
    public static SeasonalityMonthlyReturnsFrame Resolve(
        double tableFontSize,
        int columnCount,
        int rowCount = SeasonalityMonthlyReturnsTable.RowCount)
    {
        double scale = FontScale(tableFontSize);
        double cellWidth = Math.Ceiling(CellWidth * scale);
        double cellHeight = Math.Ceiling(CellHeight * scale);
        double rowHeaderWidth = Math.Ceiling(RowHeaderWidth * scale);
        int columns = Math.Max(0, columnCount);
        int rows = Math.Max(0, rowCount);
        return new SeasonalityMonthlyReturnsFrame(
            cellWidth,
            cellHeight,
            rowHeaderWidth,
            rowHeaderWidth + (columns * cellWidth),
            (HeaderRowCount + rows) * cellHeight);
    }
}

/// <summary>One rendered cell: pre-formatted English text, its colour bucket and the resolved brushes.</summary>
public sealed class SeasonalityMonthlyReturnCellPresentation
{
    internal SeasonalityMonthlyReturnCellPresentation(
        string text,
        SeasonalityMonthlyReturnSign sign,
        IBrush cellBackground,
        IBrush cellForeground)
    {
        Text = text;
        Sign = sign;
        CellBackground = cellBackground;
        CellForeground = cellForeground;
    }

    /// <summary>Signed percent (e.g. "+12.3%") or <see cref="SeasonalityMonthlyReturnsPresentation.MissingCellText"/>.</summary>
    public string Text { get; }

    public SeasonalityMonthlyReturnSign Sign { get; }

    public bool IsPositive => Sign == SeasonalityMonthlyReturnSign.Positive;

    public bool IsNegative => Sign == SeasonalityMonthlyReturnSign.Negative;

    /// <summary>Settings-resolved cell background for this cell's sign bucket.</summary>
    public IBrush CellBackground { get; }

    /// <summary>Settings-resolved text colour for this cell's sign bucket.</summary>
    public IBrush CellForeground { get; }
}

/// <summary>One rendered row: a header label plus one cell per year column and the average column.</summary>
public sealed class SeasonalityMonthlyReturnsRowPresentation
{
    internal SeasonalityMonthlyReturnsRowPresentation(
        string header,
        bool isAnnualRow,
        IReadOnlyList<SeasonalityMonthlyReturnCellPresentation> cells)
    {
        Header = header;
        IsAnnualRow = isAnnualRow;
        Cells = cells;
    }

    public string Header { get; }

    public bool IsAnnualRow { get; }

    /// <summary>One cell per <see cref="SeasonalityMonthlyReturnsPresentation.ColumnHeaders"/> entry, same order.</summary>
    public IReadOnlyList<SeasonalityMonthlyReturnCellPresentation> Cells { get; }
}

/// <summary>
/// View-ready projection of a <see cref="SeasonalityMonthlyReturnsTable"/>: English column and row
/// headers, pre-formatted signed-percent cell text ("—" when undefined) and a colour bucket per
/// cell. Screen text is always English (InvariantCulture) regardless of the UI locale — an
/// intentional exception, matching the feature requirement.
/// </summary>
public sealed class SeasonalityMonthlyReturnsPresentation
{
    /// <summary>Custom numeric format: explicit sign, one decimal, percent. Used with InvariantCulture.</summary>
    private const string ReturnFormat = "+0.0%;-0.0%;0.0%";

    /// <summary>Two-digit year prefix, e.g. <c>'22</c>.</summary>
    private const string YearLabelPrefix = "'";

    /// <summary>Text shown for an undefined cell.</summary>
    public const string MissingCellText = "—";

    private SeasonalityMonthlyReturnsPresentation(
        bool hasData,
        IReadOnlyList<string> columnHeaders,
        IReadOnlyList<SeasonalityMonthlyReturnsRowPresentation> rows,
        double fontSize,
        SeasonalityMonthlyReturnsFrame frame)
    {
        HasData = hasData;
        ColumnHeaders = columnHeaders;
        Rows = rows;
        FontSize = fontSize;
        Frame = frame;
    }

    public bool HasData { get; }

    /// <summary>Column headers: year labels and average, or month labels and annual.</summary>
    public IReadOnlyList<string> ColumnHeaders { get; }

    /// <summary>Table rows: months and annual, or years and average.</summary>
    public IReadOnlyList<SeasonalityMonthlyReturnsRowPresentation> Rows { get; }

    /// <summary>Settings-resolved font size (points) for every table cell and header.</summary>
    public double FontSize { get; }

    /// <summary>
    /// Pixel geometry for the frame: cell / row-header size scaled to <see cref="FontSize"/> and the
    /// total frame sized to the columns actually shown. The view binds cell and frame dimensions to
    /// this so the frame follows the font and the horizontal scroll extent is honest (C2 / S4c).
    /// </summary>
    public SeasonalityMonthlyReturnsFrame Frame { get; }

    /// <summary>
    /// Sign bucket for a return amount (decimal domain). The single rule shared by the monthly-returns
    /// table cells and the plot legend's annual-return colour, so the legend never re-derives it:
    /// <see cref="SeasonalityChartConstants.MonthlyReturnColorEpsilon"/> is the flat band.
    /// </summary>
    public static SeasonalityMonthlyReturnSign ClassifyReturnSign(decimal amount)
    {
        decimal epsilon = SeasonalityChartConstants.MonthlyReturnColorEpsilon;
        return amount > epsilon ? SeasonalityMonthlyReturnSign.Positive
            : amount < -epsilon ? SeasonalityMonthlyReturnSign.Negative
            : SeasonalityMonthlyReturnSign.Neutral;
    }

    /// <summary>An empty presentation (no data) — the fallback when the table has no overlaid years.</summary>
    public static SeasonalityMonthlyReturnsPresentation Empty { get; } =
        new(false, Array.Empty<string>(), Array.Empty<SeasonalityMonthlyReturnsRowPresentation>(),
            SeasonalityMonthlyReturnsStyle.Default.FontSize,
            SeasonalityMonthlyReturnsMetrics.Resolve(SeasonalityMonthlyReturnsStyle.Default.FontSize, 0));

    /// <summary>
    /// Three-argument overload that applies <see cref="SeasonalityMonthlyReturnsStyle.Default"/>. Kept
    /// for tests and design-time; the running app always supplies a settings-driven style.
    /// </summary>
    public static SeasonalityMonthlyReturnsPresentation Create(
        SeasonalityMonthlyReturnsTable table,
        string annualRowLabel,
        string averageColumnLabel)
        => Create(table, annualRowLabel, averageColumnLabel, SeasonalityMonthlyReturnsStyle.Default, SeasonalityMonthlyTableOrientation.MonthsAsRows);

    /// <summary>
    /// Projects <paramref name="table"/> for display. <paramref name="annualRowLabel"/> and
    /// <paramref name="averageColumnLabel"/> are supplied by the caller (resolved from the locale
    /// files, whose value is English in every language). <paramref name="style"/> carries the
    /// settings-resolved cell brushes and font size. <paramref name="orientation"/> controls row/column transposition.
    /// Returns <see cref="Empty"/> when the table has no years.
    /// </summary>
    public static SeasonalityMonthlyReturnsPresentation Create(
        SeasonalityMonthlyReturnsTable table,
        string annualRowLabel,
        string averageColumnLabel,
        SeasonalityMonthlyReturnsStyle style,
        SeasonalityMonthlyTableOrientation orientation = SeasonalityMonthlyTableOrientation.MonthsAsRows)
    {
        if (table is null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        if (table.Years.Count == 0)
        {
            return Empty;
        }

        string[] abbreviatedMonths = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames;

        if (orientation == SeasonalityMonthlyTableOrientation.MonthsAsColumns)
        {
            // Transposed: Columns = Months (12) + Annual, Rows = Years + Avg
            var columnHeaders = new string[SeasonalityMonthlyReturnsTable.RowCount];
            for (int i = 0; i < SeasonalitySharedAxisFormatting.MonthsPerYear; i++)
            {
                int month = (int)(((table.StartMonth - 1 + i) % SeasonalitySharedAxisFormatting.MonthsPerYear) + 1);
                columnHeaders[i] = abbreviatedMonths[month - 1];
            }
            columnHeaders[SeasonalityMonthlyReturnsTable.AnnualRowIndex] = annualRowLabel;

            int rowCount = table.Years.Count + 1;
            var rows = new SeasonalityMonthlyReturnsRowPresentation[rowCount];

            for (int yearIndex = 0; yearIndex < table.Years.Count; yearIndex++)
            {
                string header = FormatYear(table.Years[yearIndex]);
                var cells = new SeasonalityMonthlyReturnCellPresentation[SeasonalityMonthlyReturnsTable.RowCount];
                for (int m = 0; m < SeasonalitySharedAxisFormatting.MonthsPerYear; m++)
                {
                    cells[m] = BuildCell(table.Cell(m, yearIndex).Return, style);
                }
                cells[SeasonalityMonthlyReturnsTable.AnnualRowIndex] = BuildCell(table.Cell(SeasonalityMonthlyReturnsTable.AnnualRowIndex, yearIndex).Return, style);
                rows[yearIndex] = new SeasonalityMonthlyReturnsRowPresentation(header, false, cells);
            }

            var avgCells = new SeasonalityMonthlyReturnCellPresentation[SeasonalityMonthlyReturnsTable.RowCount];
            for (int m = 0; m < SeasonalitySharedAxisFormatting.MonthsPerYear; m++)
            {
                avgCells[m] = BuildCell(table.RowAverages[m], style);
            }
            avgCells[SeasonalityMonthlyReturnsTable.AnnualRowIndex] = BuildCell(table.RowAverages[SeasonalityMonthlyReturnsTable.AnnualRowIndex], style);
            rows[table.Years.Count] = new SeasonalityMonthlyReturnsRowPresentation(averageColumnLabel, true, avgCells);

            return new SeasonalityMonthlyReturnsPresentation(
                true,
                columnHeaders,
                rows,
                style.FontSize,
                SeasonalityMonthlyReturnsMetrics.Resolve(style.FontSize, columnHeaders.Length, rowCount));
        }
        else
        {
            // Classic (MonthsAsRows): Columns = Years + Avg, Rows = Months (12) + Annual
            var columnHeaders = new string[table.Years.Count + 1];
            for (int yearIndex = 0; yearIndex < table.Years.Count; yearIndex++)
            {
                columnHeaders[yearIndex] = FormatYear(table.Years[yearIndex]);
            }

            columnHeaders[^1] = averageColumnLabel;

            var rows = new SeasonalityMonthlyReturnsRowPresentation[SeasonalityMonthlyReturnsTable.RowCount];
            for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                bool isAnnual = rowIndex == SeasonalityMonthlyReturnsTable.AnnualRowIndex;
                string header;
                if (isAnnual)
                {
                    header = annualRowLabel;
                }
                else
                {
                    int month = (int)(((table.StartMonth - 1 + rowIndex) % SeasonalitySharedAxisFormatting.MonthsPerYear) + 1);
                    header = abbreviatedMonths[month - 1];
                }

                var cells = new SeasonalityMonthlyReturnCellPresentation[columnHeaders.Length];
                for (int yearIndex = 0; yearIndex < table.Years.Count; yearIndex++)
                {
                    cells[yearIndex] = BuildCell(table.Cell(rowIndex, yearIndex).Return, style);
                }

                cells[^1] = BuildCell(table.RowAverages[rowIndex], style);
                rows[rowIndex] = new SeasonalityMonthlyReturnsRowPresentation(header, isAnnual, cells);
            }

            return new SeasonalityMonthlyReturnsPresentation(
                true,
                columnHeaders,
                rows,
                style.FontSize,
                SeasonalityMonthlyReturnsMetrics.Resolve(style.FontSize, columnHeaders.Length, rows.Length));
        }
    }

    private static SeasonalityMonthlyReturnCellPresentation BuildCell(decimal? value, SeasonalityMonthlyReturnsStyle style)
    {
        if (value is not { } amount)
        {
            (IBrush missingBackground, IBrush missingForeground) = style.Resolve(SeasonalityMonthlyReturnSign.Neutral);
            return new SeasonalityMonthlyReturnCellPresentation(
                MissingCellText, SeasonalityMonthlyReturnSign.Neutral, missingBackground, missingForeground);
        }

        SeasonalityMonthlyReturnSign sign = ClassifyReturnSign(amount);

        (IBrush background, IBrush foreground) = style.Resolve(sign);
        return new SeasonalityMonthlyReturnCellPresentation(
            amount.ToString(ReturnFormat, CultureInfo.InvariantCulture),
            sign,
            background,
            foreground);
    }

    private static string FormatYear(int year) =>
        YearLabelPrefix + (year % 100).ToString("00", CultureInfo.InvariantCulture);
}
