using System.Reflection;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>One name a Screener condition can read besides an indicator: a price, a column of the ticker table, or a pattern.</summary>
internal sealed record ScreenerCatalogName(string Name, string Group, string Description, bool IsText);

/// <summary>
/// The Columns, Criteria (patterns) and price sources a Screener condition can use, taken from the application's own
/// <see cref="IScreenerCatalogProvider"/> (the list the Screener tab shows), so no list is kept here.
/// <para>
/// A column is looked up by a text key: the value extractor reads the first of CustomDisplayName, OutputName and DisplayName and matches it by
/// keywords. The Screener tab stores the long description as the first key, and for many columns that text resolves to no metric or to another one
/// (measured 2026-10-22). This catalogue therefore uses the short name as the key, and verifies every column against the extractor itself: a column
/// is offered only when a probe metadata whose properties all hold different values is read back as exactly the property the column names. A column
/// that fails the check is listed as unavailable instead of silently evaluating to a wrong metric.
/// </para>
/// </summary>
internal sealed class ScreenerCatalog
{
    private readonly Lazy<Items> _items;

    public ScreenerCatalog(IScreenerCatalogProvider catalogProvider, IIndicatorFactory factory)
    {
        _items = new Lazy<Items>(() => Items.Build(catalogProvider.GetCatalogItems(factory)));
    }

    /// <summary>Columns a condition can use, in catalogue order. A text column (Sector, Industry, Region, Name, Tag) is compared with text, every other one with a number.</summary>
    public IReadOnlyList<ScreenerCatalogName> Columns => _items.Value.ColumnItems
        .Where(item => _items.Value.Evaluable.Contains(item.ShortName))
        .Select(item => ToName(item, IsTextColumn(item.ShortName))).ToList();

    /// <summary>Columns of the ticker table that no numeric or text condition can read reliably (descriptive text, timestamps, or a name the extractor resolves to another metric).</summary>
    public IReadOnlyList<string> UnavailableColumns => _items.Value.ColumnItems
        .Where(item => !_items.Value.Evaluable.Contains(item.ShortName)).Select(item => item.ShortName).ToList();

    /// <summary>Patterns and rules (candlestick, chart, harmonic, wave, market structure) in catalogue order; each one is true or false at a bar.</summary>
    public IReadOnlyList<ScreenerCatalogName> Criteria => _items.Value.CriteriaItems.Select(item => ToName(item, false)).ToList();

    /// <summary>The price sources, in the order of the Screener tab.</summary>
    public IReadOnlyList<ScreenerCatalogName> Prices => _items.Value.PriceItems.Select(item => ToName(item, false)).ToList();

    /// <summary>The column of that short name (case-insensitive), whether or not it can be evaluated; see <see cref="IsEvaluable"/>.</summary>
    public ScreenerCatalogItem? FindColumn(string name) => Find(_items.Value.ColumnItems, name);

    public bool IsEvaluable(ScreenerCatalogItem column) => _items.Value.Evaluable.Contains(column.ShortName);

    public ScreenerCatalogItem? FindCriteria(string name) => Find(_items.Value.CriteriaItems, name);

    public ScreenerCatalogItem? FindPrice(string name) => Find(_items.Value.PriceItems, name);

    /// <summary>True when the column named <paramref name="name"/> holds text (compared with Equal, NotEqual, Contains or DoesNotContain).</summary>
    public static bool IsTextColumn(string name)
        => ScreenerTextFields.Names.Any(text => string.Equals(text, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The side of a condition that reads <paramref name="column"/>: the short name is the lookup key and the description is left out (see the type remarks).</summary>
    public static ScreenerIndicatorSideConfig ColumnSide(ScreenerCatalogItem column, int offset)
        => new()
        {
            OutputName = column.ShortName,
            CategoryType = ScreenerItemCategoryType.Column,
            TimeFrame = TimeFrame.D1,
            Offset = offset,
        };

    /// <summary>
    /// True when the Column <paramref name="side"/> is read from the bars of its own time frame (Open, High, Low, Close, Volume, Change, Change %): the value of a bar is
    /// then the value on that bar's date. Every other column is read from the stored metadata, a snapshot that holds today's value whatever date is evaluated.
    /// The one definition is <see cref="ScreenerValueExtractor.IsCandleValueColumn"/> (the same that gives a side its time frame), so a side stored under any of the column's names is
    /// classified as it is evaluated; <c>ScreenerBarColumnDefinitionTests</c> pins the seven bar columns among the catalog's columns.
    /// </summary>
    public static bool ReadsFromBars(ScreenerIndicatorSideConfig side) => ScreenerValueExtractor.IsCandleValueColumn(side);

    /// <summary>The bars of the probe reads: two bars, so that Change and Change % have a previous close.</summary>
    private static readonly IReadOnlyList<CandleData> ProbeBars = new List<CandleData>
    {
        new() { Timestamp = new DateTime(2024, 1, 1), Open = 10m, High = 12m, Low = 9m, Close = 11m, Volume = 100 },
        new() { Timestamp = new DateTime(2024, 1, 2), Open = 11m, High = 13m, Low = 10m, Close = 12m, Volume = 200 },
    };

    private static ScreenerCatalogItem? Find(IReadOnlyList<ScreenerCatalogItem> items, string name)
        => items.FirstOrDefault(item => string.Equals(item.ShortName, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static ScreenerCatalogName ToName(ScreenerCatalogItem item, bool isText)
        => new(item.ShortName, item.GroupName, item.DisplayName, isText);

    private sealed class Items
    {
        public required IReadOnlyList<ScreenerCatalogItem> ColumnItems { get; init; }

        public required IReadOnlyList<ScreenerCatalogItem> CriteriaItems { get; init; }

        public required IReadOnlyList<ScreenerCatalogItem> PriceItems { get; init; }

        public required HashSet<string> Evaluable { get; init; }

        public static Items Build(IReadOnlyList<ScreenerCatalogItem> all)
        {
            List<ScreenerCatalogItem> columns = all.Where(i => i.CategoryType == ScreenerItemCategoryType.Column).ToList();
            return new Items
            {
                ColumnItems = columns,
                CriteriaItems = all.Where(i => i.CategoryType == ScreenerItemCategoryType.Criteria).ToList(),
                PriceItems = all.Where(i => i.CategoryType == ScreenerItemCategoryType.Indicator && i.IndicatorType == IndicatorType.Price).ToList(),
                Evaluable = ColumnProbe.EvaluableColumns(columns),
            };
        }
    }

    /// <summary>Reads every column back from a metadata object whose properties hold distinct values.</summary>
    private static class ColumnProbe
    {
        /// <summary>The first probe value; each property gets the next whole number so no two properties share a value (and an int or long property keeps it).</summary>
        private const int FirstProbeValue = 1001;

        public static HashSet<string> EvaluableColumns(IReadOnlyList<ScreenerCatalogItem> columns)
        {
            (TickerMetadata metadata, Dictionary<string, decimal> expected) = ProbeMetadata();
            IReadOnlyList<CandleData> bars = ProbeBars;

            var evaluable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ScreenerCatalogItem column in columns)
            {
                if (IsTextColumn(column.ShortName))
                {
                    evaluable.Add(column.ShortName);
                    continue;
                }

                ScreenerIndicatorSideConfig side = ColumnSide(column, 0);

                // A value that is there without any metadata is read from the bars (Open, High, Low, Close, Volume, Change, Change %).
                if (ReadsFromBars(side))
                {
                    evaluable.Add(column.ShortName);
                    continue;
                }

                if (column.ColumnMemberName is { } member
                    && expected.TryGetValue(member, out decimal value)
                    && ScreenerValueExtractor.Default.ExtractValueNullable(side, bars, metadata) == value)
                {
                    evaluable.Add(column.ShortName);
                }
            }

            return evaluable;
        }

        private static (TickerMetadata Metadata, Dictionary<string, decimal> Expected) ProbeMetadata()
        {
            object boxed = TickerMetadata.Unknown;
            var expected = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            int next = FirstProbeValue;
            foreach (PropertyInfo property in typeof(TickerMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.SetMethod is null)
                {
                    continue;
                }

                Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                object? value;
                decimal read;
                if (type == typeof(bool))
                {
                    value = true;
                    read = 1m;
                }
                else if (type == typeof(decimal))
                {
                    value = (decimal)next;
                    read = next++;
                }
                else if (type == typeof(long))
                {
                    value = (long)next;
                    read = next++;
                }
                else if (type == typeof(int))
                {
                    value = next;
                    read = next++;
                }
                else if (type == typeof(double))
                {
                    value = (double)next;
                    read = next++;
                }
                else
                {
                    continue;
                }

                property.SetValue(boxed, value);
                expected[property.Name] = read;
            }

            return ((TickerMetadata)boxed, expected);
        }
    }
}
