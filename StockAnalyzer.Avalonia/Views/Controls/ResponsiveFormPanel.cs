using System;
using Avalonia;
using Avalonia.Controls;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// A panel designed for two-column responsive form layouts.
/// Distributes fields across up to <see cref="MaxColumns"/> columns when width permits,
/// and falls back to a single column when available space or child desired width requires it.
/// Supports <see cref="IsFullWidthProperty"/> for full-width spanning rows.
/// </summary>
public class ResponsiveFormPanel : Panel
{
    private const double WidthTolerance = 0.5;

    public static readonly StyledProperty<double> MinFieldWidthProperty =
        AvaloniaProperty.Register<ResponsiveFormPanel, double>(
            nameof(MinFieldWidth), LayoutConstants.IndicatorFormMinFieldWidth);

    public static readonly StyledProperty<double> MaxFieldWidthProperty =
        AvaloniaProperty.Register<ResponsiveFormPanel, double>(
            nameof(MaxFieldWidth), LayoutConstants.IndicatorFormMaxFieldWidth);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ResponsiveFormPanel, double>(
            nameof(ColumnSpacing), LayoutConstants.IndicatorFormColumnSpacing);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ResponsiveFormPanel, double>(
            nameof(RowSpacing), LayoutConstants.IndicatorFormRowSpacing);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<ResponsiveFormPanel, int>(
            nameof(MaxColumns), LayoutConstants.IndicatorFormMaxColumns);

    public static readonly AttachedProperty<bool> IsFullWidthProperty =
        AvaloniaProperty.RegisterAttached<ResponsiveFormPanel, Control, bool>(
            "IsFullWidth", false);

    public double MinFieldWidth
    {
        get => GetValue(MinFieldWidthProperty);
        set => SetValue(MinFieldWidthProperty, value);
    }

    public double MaxFieldWidth
    {
        get => GetValue(MaxFieldWidthProperty);
        set => SetValue(MaxFieldWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public static bool GetIsFullWidth(Control element) => element.GetValue(IsFullWidthProperty);
    public static void SetIsFullWidth(Control element, bool value) => element.SetValue(IsFullWidthProperty, value);

    static ResponsiveFormPanel()
    {
        AffectsMeasure<ResponsiveFormPanel>(
            MinFieldWidthProperty,
            MaxFieldWidthProperty,
            ColumnSpacingProperty,
            RowSpacingProperty,
            MaxColumnsProperty);
        AffectsArrange<ResponsiveFormPanel>(
            MinFieldWidthProperty,
            MaxFieldWidthProperty,
            ColumnSpacingProperty,
            RowSpacingProperty,
            MaxColumnsProperty);
        AffectsMeasure<Control>(IsFullWidthProperty);
        AffectsArrange<Control>(IsFullWidthProperty);
    }

    /// <summary>
    /// Cached single-column fallback state computed during <see cref="MeasureOverride"/>
    /// and consumed by <see cref="ArrangeOverride"/>.
    /// Avalonia's layout engine guarantees that Measure precedes Arrange on the UI thread.
    /// </summary>
    private bool _isSingleColumnFallback;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        int count = children.Count;
        int visibleCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (children[i].IsVisible) visibleCount++;
        }

        if (visibleCount == 0)
        {
            _isSingleColumnFallback = false;
            return new Size(0, 0);
        }

        double minFieldWidth = Math.Max(0.0, MinFieldWidth);
        double maxFieldWidth = Math.Max(minFieldWidth, MaxFieldWidth);
        double colSpacing = Math.Max(0.0, ColumnSpacing);
        double rowSpacing = Math.Max(0.0, RowSpacing);
        int maxCols = Math.Max(1, MaxColumns);

        double maxContentWidth = maxCols * maxFieldWidth + (maxCols - 1) * colSpacing;
        double w = availableSize.Width;
        if (double.IsNaN(w) || double.IsInfinity(w))
        {
            w = maxContentWidth;
        }
        double wp = Math.Min(w, maxContentWidth);
        if (wp < 0) wp = 0;

        double divisor = minFieldWidth + colSpacing;
        int cols = divisor > 0
            ? Math.Clamp((int)Math.Floor((wp + colSpacing) / divisor), 1, maxCols)
            : 1;

        double colW = cols > 1 ? (wp - (cols - 1) * colSpacing) / cols : wp;
        if (colW < 0) colW = 0;

        bool needFallback = false;
        for (int i = 0; i < count; i++)
        {
            var child = children[i];
            if (!child.IsVisible) continue;

            bool isFull = GetIsFullWidth(child);
            double itemWidth = isFull ? wp : colW;
            child.Measure(new Size(itemWidth, double.PositiveInfinity));

            double minWidth = (child as global::Avalonia.Layout.Layoutable)?.MinWidth ?? 0;
            if (cols > 1 && !isFull && (minWidth > colW + WidthTolerance || child.DesiredSize.Width > colW + WidthTolerance))
            {
                needFallback = true;
            }
        }

        _isSingleColumnFallback = needFallback && cols > 1;
        if (_isSingleColumnFallback)
        {
            cols = 1;
            colW = wp;
            for (int i = 0; i < count; i++)
            {
                var child = children[i];
                if (!child.IsVisible) continue;
                child.Measure(new Size(wp, double.PositiveInfinity));
            }
        }

        double totalHeight = 0;
        bool isFirstRow = true;
        int iIdx = 0;
        while (iIdx < count)
        {
            var firstChild = children[iIdx];
            if (!firstChild.IsVisible)
            {
                iIdx++;
                continue;
            }

            if (!isFirstRow)
            {
                totalHeight += rowSpacing;
            }
            isFirstRow = false;

            if (GetIsFullWidth(firstChild))
            {
                totalHeight += firstChild.DesiredSize.Height;
                iIdx++;
            }
            else
            {
                double rowHeight = firstChild.DesiredSize.Height;
                int itemsInRow = 1;
                int next = iIdx + 1;
                while (itemsInRow < cols && next < count)
                {
                    var nextChild = children[next];
                    if (nextChild.IsVisible)
                    {
                        if (GetIsFullWidth(nextChild))
                        {
                            break;
                        }
                        rowHeight = Math.Max(rowHeight, nextChild.DesiredSize.Height);
                        itemsInRow++;
                    }
                    next++;
                }

                totalHeight += rowHeight;
                iIdx = next;
            }
        }

        return new Size(wp, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        int count = children.Count;

        double minFieldWidth = Math.Max(0.0, MinFieldWidth);
        double maxFieldWidth = Math.Max(minFieldWidth, MaxFieldWidth);
        double colSpacing = Math.Max(0.0, ColumnSpacing);
        double rowSpacing = Math.Max(0.0, RowSpacing);
        int maxCols = Math.Max(1, MaxColumns);

        double maxContentWidth = maxCols * maxFieldWidth + (maxCols - 1) * colSpacing;
        double w = finalSize.Width;
        if (double.IsNaN(w) || double.IsInfinity(w))
        {
            w = maxContentWidth;
        }
        double wp = Math.Min(w, maxContentWidth);
        if (wp < 0) wp = 0;

        double divisor = minFieldWidth + colSpacing;
        int cols = divisor > 0
            ? Math.Clamp((int)Math.Floor((wp + colSpacing) / divisor), 1, maxCols)
            : 1;

        double colW = cols > 1 ? (wp - (cols - 1) * colSpacing) / cols : wp;
        if (colW < 0) colW = 0;

        if (!_isSingleColumnFallback && cols > 1)
        {
            for (int i = 0; i < count; i++)
            {
                var child = children[i];
                if (!child.IsVisible || GetIsFullWidth(child)) continue;
                if (child.DesiredSize.Width > colW + WidthTolerance)
                {
                    _isSingleColumnFallback = true;
                    break;
                }
            }
        }

        if (_isSingleColumnFallback && cols > 1)
        {
            cols = 1;
            colW = wp;
        }

        double y = 0;
        bool isFirstRow = true;
        int iIdx = 0;
        while (iIdx < count)
        {
            var firstChild = children[iIdx];
            if (!firstChild.IsVisible)
            {
                iIdx++;
                continue;
            }

            if (!isFirstRow)
            {
                y += rowSpacing;
            }
            isFirstRow = false;

            if (GetIsFullWidth(firstChild))
            {
                double rowHeight = firstChild.DesiredSize.Height;
                firstChild.Arrange(new Rect(0, y, wp, rowHeight));
                y += rowHeight;
                iIdx++;
            }
            else
            {
                double rowHeight = firstChild.DesiredSize.Height;
                int itemsInRow = 1;
                int next = iIdx + 1;
                while (itemsInRow < cols && next < count)
                {
                    var nextChild = children[next];
                    if (nextChild.IsVisible)
                    {
                        if (GetIsFullWidth(nextChild))
                        {
                            break;
                        }
                        rowHeight = Math.Max(rowHeight, nextChild.DesiredSize.Height);
                        itemsInRow++;
                    }
                    next++;
                }

                double x = 0;
                for (int k = iIdx; k < next; k++)
                {
                    var child = children[k];
                    if (!child.IsVisible) continue;

                    child.Arrange(new Rect(x, y, colW, rowHeight));
                    x += colW + colSpacing;
                }

                y += rowHeight;
                iIdx = next;
            }
        }

        return new Size(wp, y);
    }
}
